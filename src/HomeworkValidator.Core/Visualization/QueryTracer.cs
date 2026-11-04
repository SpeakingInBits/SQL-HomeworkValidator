using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Visualization;

/// <summary>What came of tracing a query: the trace, or why there isn't one.</summary>
public sealed record TraceOutcome(QueryTrace? Trace, IReadOnlyList<string> Problems);

/// <summary>
/// Finds out what a student's query does to each row, by having SQL Server run it with row numbers on each table.
/// Everything runs as the read-only database user, like any student query.
/// </summary>
public sealed class QueryTracer(ServerConnection server)
{
    /// <summary>The most rows of any table to show. Beyond this, an animation is too long to watch.</summary>
    public const int MaxRows = 200;

    /// <summary>The most left and right row pairs to compare for the in-depth join view.</summary>
    public const int MaxComparisons = 20_000;

    // Comma joins return every pair of rows before WHERE, so this allows far more rows than a student's result may have.
    private readonly QueryRunner _queries = new(server) { MaxRows = 200_000 };

    /// <exception cref="QueryFailedException">The student's query failed, or the traced version of it did.</exception>
    public async Task<TraceOutcome> TraceAsync(string database, string sql, CancellationToken ct = default)
    {
        var shape = QueryShape.Analyze(sql);
        if (shape.Problems.Count > 0)
            return new TraceOutcome(null, shape.Problems);

        // Their own query first, so a mistake in it is reported in their words, with its line number.
        var actual = await _queries.RunAsync(database, shape.Query, restricted: true, ct);

        var orderings = new List<string>();
        foreach (var source in shape.Sources)
            orderings.Add(await RowOrderingAsync(database, source, ct));

        try
        {
            var trace = shape.IsGrouped || shape.IsWindowed
                ? await TraceGroupsAsync(database, shape, orderings, actual.Rows.Count, ct)
                : await TraceRowsAsync(database, shape, orderings, actual.Rows.Count, ct);
            return new TraceOutcome(trace, []);
        }
        catch (QueryFailedException ex)
        {
            // It can fail where the real query doesn't, as these queries also work on rows WHERE would have removed.
            throw new QueryFailedException($"The visualizer couldn't follow this query. {ex.Message}");
        }
    }

    /// <summary>
    /// How to number a table's rows so that they get the same numbers in every query: by its primary key, or by all of
    /// its columns (rows that tie then are identical, so it doesn't matter which is which).
    /// </summary>
    private async Task<string> RowOrderingAsync(string database, ShapeSource source, CancellationToken ct)
    {
        var name = source.Table.Replace("'", "''");
        var columns = await _queries.RunAsync(database, $"""
            SELECT c.name, ic.key_ordinal
            FROM sys.columns c
                LEFT JOIN sys.indexes i ON i.object_id = c.object_id AND i.is_primary_key = 1
                LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(N'{name}')
                AND TYPE_NAME(c.system_type_id) NOT IN ('text', 'ntext', 'image', 'xml', 'geography', 'geometry', 'hierarchyid', 'sql_variant')
            ORDER BY ic.key_ordinal, c.column_id;
            """, restricted: true, ct);

        var keys = columns.Rows.Where(r => r[1] is not null).Select(r => (string)r[0]!).ToList();
        var chosen = keys.Count > 0 ? keys : columns.Rows.Select(r => (string)r[0]!).ToList();
        return chosen.Count > 0
            ? string.Join(", ", chosen.Select(c => $"{QueryShape.Quote(source.Reference)}.{QueryShape.Quote(c)}"))
            : "(SELECT NULL)";
    }

    /// <summary>A query without grouping: one query reports every row, its WHERE results and its place in the ORDER BY.</summary>
    private async Task<QueryTrace> TraceRowsAsync(string database, QueryShape shape, List<string> orderings, int actualRows, CancellationToken ct)
    {
        var pipeline = await _queries.RunAsync(database, shape.PipelineSql(orderings), restricted: true, ct);

        var firstKey = pipeline.Columns.Count - shape.Conditions.Count - shape.Sources.Count;
        var selected = firstKey - shape.ExtraColumns.Count;

        // SELECT * also picks up the row numbers, which aren't part of the query's result.
        var shown = OwnColumns(pipeline, selected);
        var columns = shown.Select(c => Header(pipeline.Columns[c])).Concat(shape.ExtraColumns).ToList();
        var resultColumns = shown.Count;
        shown.AddRange(Enumerable.Range(selected, shape.ExtraColumns.Count));

        // Rows arrive in ORDER BY order, which is their position in the sorted table.
        var arrived = ReadRows(pipeline, shown, firstKey, shape, groupColumn: null);
        var rows = Keep(arrived);
        var rowOf = rows.Select((r, i) => (r.Arrival, i)).ToDictionary(x => x.Arrival, x => x.i);

        var sort = shape.SortKeys.Count > 0
            ? new TraceSort(shape.SortKeys, arrived.Where(r => rowOf.ContainsKey(r.Arrival) && r.Passes).Select(r => rowOf[r.Arrival]).ToList())
            : null;

        return await FinishAsync(database, shape, orderings, rows, arrived.Count, columns, resultColumns, sort, null, null, actualRows, ct);
    }

    /// <summary>
    /// GROUP BY and window functions: the rows and what they turn into can't come from one query, as the
    /// select list only makes sense after grouping. Group numbers connect them.
    /// </summary>
    private async Task<QueryTrace> TraceGroupsAsync(string database, QueryShape shape, List<string> orderings, int actualRows, CancellationToken ct)
    {
        var table = await _queries.RunAsync(database, shape.GroupRowsSql(orderings), restricted: true, ct);
        var valueCount = shape.RowColumns.Count;
        var arrived = ReadRows(table, Enumerable.Range(0, valueCount).ToList(), valueCount, shape, groupColumn: table.Columns.Count - 1);
        var rows = Keep(arrived);
        var columns = shape.RowColumns.Select(c => c.Header).ToList();

        var (grouping, sort) = shape.IsGrouped
            ? await GroupByAsync(database, shape, orderings, rows, ct)
            : await WindowAsync(database, shape, orderings, rows, ct);

        var note = shape.MixedPartitions ? "This query's window functions use different partitions. This shows the first one's." : null;
        return await FinishAsync(database, shape, orderings, rows, arrived.Count, columns, columns.Count, sort, grouping, note, actualRows, ct);
    }

    private async Task<(TraceGrouping, TraceSort?)> GroupByAsync(
        string database, QueryShape shape, List<string> orderings, List<PipelineRow> rows, CancellationToken ct)
    {
        var result = await _queries.RunAsync(database, shape.GroupResultSql(orderings), restricted: true, ct);
        var testCount = shape.HavingConditions.Count;
        var selected = result.Columns.Count - 2 - testCount;
        var display = ResultComparer.Rows(result, result.Rows.Count).Rows;

        int Group(int row) => Convert.ToInt32(result.Rows[row][selected]) - 1;
        bool IsGroup(int row) => Convert.ToInt32(result.Rows[row][selected + 1]) == 0;
        bool Test(int row, int condition) => Convert.ToInt32(result.Rows[row][selected + 2 + condition]) != 0;

        // Groups in order, so a group's position is its number, and then the rows ROLLUP or CUBE added.
        var all = Enumerable.Range(0, result.Rows.Count).ToList();
        var order = all.Where(IsGroup).OrderBy(Group).Concat(all.Where(r => !IsGroup(r))).ToList();
        var positions = order.Select((row, position) => (row, position)).ToDictionary(x => x.row, x => x.position);

        var having = shape.Having is { } text
            ? new TraceFilter(text, shape.HavingConditions,
                Enumerable.Range(0, testCount).Select(c => (IReadOnlyList<bool>)order.Select(r => Test(r, c)).ToList()).ToList(),
                order.Select(r => Enumerable.Range(0, testCount).All(c => Test(r, c))).ToList())
            : null;

        var groupCount = all.Count(IsGroup);
        var grouping = new TraceGrouping(
            Collapses: true,
            shape.GroupClause,
            rows.Select(r => r.Group).ToList(),
            Labels(shape, rows, groupCount),
            Enumerable.Range(0, selected).Select(c => Header(result.Columns[c])).ToList(),
            order.Select(r => (IReadOnlyList<string>)display[r].Take(selected).ToList()).ToList(),
            order.Select(r => IsGroup(r) ? Group(r) : -1).ToList(),
            shape.CalculatedColumns,
            having,
            shape.SummaryRows);

        var sort = shape.SortKeys.Count > 0
            ? new TraceSort(shape.SortKeys, all.Where(r => having?.Passes[positions[r]] != false).Select(r => positions[r]).ToList())
            : null;
        return (grouping, sort);
    }

    private async Task<(TraceGrouping, TraceSort?)> WindowAsync(
        string database, QueryShape shape, List<string> orderings, List<PipelineRow> rows, CancellationToken ct)
    {
        var result = await _queries.RunAsync(database, shape.WindowResultSql(orderings), restricted: true, ct);
        var sourceCount = shape.Sources.Count;
        var selected = result.Columns.Count - sourceCount;
        var shown = OwnColumns(result, selected);
        var display = ResultComparer.Rows(result, result.Rows.Count).Rows;

        // Row numbers say which row each result row is. Rows left out because there were too many have none.
        var rowOf = rows.Select((row, i) => (Key: PathKey(row.Path), i)).ToDictionary(x => x.Key, x => x.i);
        var arrivals = Enumerable.Range(0, result.Rows.Count)
            .Select(r => (Result: r, Row: rowOf.GetValueOrDefault(PathKey(Path(result.Rows[r], selected, sourceCount)), -1)))
            .Where(x => x.Row >= 0)
            .ToList();

        var order = arrivals.OrderBy(x => x.Row).ToList();
        var positions = order.Select((x, position) => (x.Result, position)).ToDictionary(x => x.Result, x => x.position);

        var grouping = new TraceGrouping(
            Collapses: false,
            shape.GroupClause,
            rows.Select(r => r.Group).ToList(),
            Labels(shape, rows, rows.Select(r => r.Group + 1).DefaultIfEmpty(0).Max()),
            shown.Select(c => Header(result.Columns[c])).ToList(),
            order.Select(x => (IReadOnlyList<string>)shown.Select(c => display[x.Result][c]).ToList()).ToList(),
            order.Select(x => x.Row).ToList(),
            shape.CalculatedColumns,
            null,
            null);

        var sort = shape.SortKeys.Count > 0 ? new TraceSort(shape.SortKeys, arrivals.Select(x => positions[x.Result]).ToList()) : null;
        return (grouping, sort);
    }

    /// <summary>Names each group by its keys, using the first of its rows.</summary>
    private static List<string> Labels(QueryShape shape, List<PipelineRow> rows, int groupCount)
    {
        var labels = new List<string>();
        for (var group = 0; group < groupCount; group++)
        {
            var row = rows.FirstOrDefault(r => r.Group == group);
            labels.Add(shape.KeyColumns == 0 ? "All rows" : row is null ? $"Group {group + 1}" : string.Join(", ", row.Cells.Take(shape.KeyColumns)));
        }

        return labels;
    }

    private static string Header(string column) => column.Length > 0 ? column : "(no name)";

    /// <summary>The first <paramref name="count"/> columns, without the row numbers that SELECT * picks up.</summary>
    private static List<int> OwnColumns(ResultTable table, int count) =>
        Enumerable.Range(0, count).Where(c => table.Columns[c] != QueryShape.RowNumberColumn).ToList();

    private static int[] Path(object?[] row, int firstKey, int sourceCount) =>
        Enumerable.Range(0, sourceCount).Select(k => row[firstKey + k] is { } number ? Convert.ToInt32(number) : QueryTrace.NoMatch).ToArray();

    private static string PathKey(int[] path) => string.Join(',', path);

    /// <summary>Reads rows laid out as: values, then a row number per source, then a result per WHERE condition.</summary>
    /// <param name="groupColumn">Where the group number is, after a column saying whether the row passes WHERE.</param>
    private static List<PipelineRow> ReadRows(ResultTable table, List<int> shown, int firstKey, QueryShape shape, int? groupColumn)
    {
        var sourceCount = shape.Sources.Count;
        var conditionCount = shape.Conditions.Count;
        var display = ResultComparer.Rows(table, table.Rows.Count).Rows;

        return table.Rows.Select((row, i) =>
        {
            var conditions = Enumerable.Range(0, conditionCount).Select(c => Convert.ToInt32(row[firstKey + sourceCount + c]) != 0).ToArray();
            var group = groupColumn is { } column && Convert.ToInt32(row[column - 1]) != 0 ? Convert.ToInt32(row[column]) - 1 : -1;
            return new PipelineRow(i, shown.Select(c => display[i][c]).ToList(), Path(row, firstKey, sourceCount), conditions, group);
        }).ToList();
    }

    /// <summary>
    /// The rows to show, in the order of their source tables. When there are too many to watch it keeps the ones
    /// that pass WHERE, as a comma join has very few of those among all its pairs.
    /// </summary>
    private static List<PipelineRow> Keep(List<PipelineRow> arrived)
    {
        var byPath = Comparer<int[]>.Create(ComparePaths);
        return arrived.OrderBy(r => r.Passes ? 0 : 1).ThenBy(r => r.Path, byPath).Take(MaxRows).OrderBy(r => r.Path, byPath).ToList();
    }

    private async Task<QueryTrace> FinishAsync(
        string database, QueryShape shape, List<string> orderings, List<PipelineRow> rows, int total,
        List<string> columns, int resultColumns, TraceSort? sort, TraceGrouping? grouping, string? note, int actualRows, CancellationToken ct)
    {
        var filter = shape.Where is { } where
            ? new TraceFilter(where, shape.Conditions,
                Enumerable.Range(0, shape.Conditions.Count).Select(c => (IReadOnlyList<bool>)rows.Select(r => r.Conditions[c]).ToList()).ToList(),
                rows.Select(r => r.Passes).ToList())
            : null;

        var sources = new List<TraceSource>();
        var paths = rows.Select(r => r.Path).ToList();
        var sourceTruncated = false;
        if (shape.Joins.Count > 0)
        {
            var positions = new List<Dictionary<int, int>>();
            for (var k = 0; k < shape.Sources.Count; k++)
            {
                var (source, position, truncated) = await ReadSourceAsync(database, shape, k, orderings[k], rows, ct);
                sources.Add(source);
                positions.Add(position);
                sourceTruncated |= truncated;
            }

            paths = paths.Select(p => p.Select((number, k) => number == QueryTrace.NoMatch ? QueryTrace.NoMatch : positions[k][number]).ToArray()).ToList();
        }

        var truncatedRows = total > rows.Count || sourceTruncated;
        var trace = new QueryTrace(
            shape.Sources[0].Table, sources, Joins(shape, sources), columns, resultColumns,
            rows.Select(r => r.Cells).ToList(), paths, filter, sort, [],
            truncatedRows ? $"This query reads {total:N0} rows, which is too many to watch, so this shows {rows.Count:N0} of them." : note)
        {
            Grouping = grouping,
        };

        trace = trace with { Cuts = Cuts(shape, trace, actualRows) };
        return shape.Joins.Count > 0
            ? await WithComparisonsAsync(database, shape, orderings, trace, truncatedRows, ct)
            : trace;
    }

    /// <summary>The cuts that DISTINCT and TOP make to the result. The real query's row count says where TOP stopped, even with PERCENT or WITH TIES.</summary>
    private static List<TraceCut> Cuts(QueryShape shape, QueryTrace trace, int actualRows)
    {
        var result = trace.Result;
        var keeps = Enumerable.Repeat(true, result.Count).ToArray();
        var cuts = new List<TraceCut>();

        if (shape.Distinct)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < result.Count; i++)
                keeps[i] = seen.Add(string.Join('\u001F', trace.ResultCells(result[i])));
            if (keeps.Contains(false))
                cuts.Add(new TraceCut("DISTINCT removes the duplicate rows", keeps.ToList()));
        }

        if (shape.Top is { } top)
        {
            var kept = 0;
            for (var i = 0; i < keeps.Length; i++)
                keeps[i] = keeps[i] && ++kept <= actualRows;
            if (kept > actualRows)
                cuts.Add(new TraceCut($"{top} keeps the first {Rows(actualRows)}", keeps.ToList()));
        }

        return cuts;
    }

    private static string Rows(int count) => count == 1 ? "row" : $"{count:N0} rows";

    /// <summary>Reads a table, with the rows the joined rows use and otherwise the first ones. Also returns each row number's position.</summary>
    private async Task<(TraceSource Source, Dictionary<int, int> Positions, bool Truncated)> ReadSourceAsync(
        string database, QueryShape shape, int index, string ordering, List<PipelineRow> joined, CancellationToken ct)
    {
        var source = shape.Sources[index];
        var table = await _queries.RunAsync(database, shape.SourceSql(source, ordering), restricted: true, ct);
        var numberColumn = table.Columns.Count - 1;
        var used = joined.Select(r => r.Path[index]).Where(n => n != QueryTrace.NoMatch).ToHashSet();

        var display = ResultComparer.Rows(table, table.Rows.Count).Rows;
        var keep = Enumerable.Range(0, table.Rows.Count)
            .Where(i => i < MaxRows || used.Contains(Convert.ToInt32(table.Rows[i][numberColumn])))
            .ToList();

        var positions = keep.Select((row, position) => (Number: Convert.ToInt32(table.Rows[row][numberColumn]), position)).ToDictionary(x => x.Number, x => x.position);
        var rows = keep.Select(row => (IReadOnlyList<string>)display[row].Take(numberColumn).ToList()).ToList();
        return (new TraceSource(source.Table, source.Alias, table.Columns.Take(numberColumn).ToList(), rows), positions, keep.Count < table.Rows.Count);
    }

    private static List<TraceJoin> Joins(QueryShape shape, List<TraceSource> sources) =>
        shape.Joins.Select((join, i) => new TraceJoin(join.Type, join.On, LeftSource(shape, sources, join, i + 1))).ToList();

    /// <summary>The nearest table before <paramref name="added"/> that the ON condition reads, or the one just before it.</summary>
    private static int LeftSource(QueryShape shape, List<TraceSource> sources, ShapeJoin join, int added)
    {
        for (var k = added - 1; k >= 0; k--)
        {
            var reference = shape.Sources[k].Reference;
            if (join.OnColumns.Any(c => c.Prefix.Length > 0
                    ? c.Prefix.Equals(reference, StringComparison.OrdinalIgnoreCase)
                    : sources[k].Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
                return k;
        }

        return added - 1;
    }

    private async Task<QueryTrace> WithComparisonsAsync(
        string database, QueryShape shape, List<string> orderings, QueryTrace trace, bool truncated, CancellationToken ct)
    {
        string? unavailable = null;
        if (shape.Joins.Count != 1)
            unavailable = "The in-depth view is for queries with one join.";
        else if (shape.Joins[0].On is null)
            unavailable = "A join without an ON condition has no comparisons to show.";
        else if (truncated || (long)trace.Sources[0].Rows.Count * trace.Sources[1].Rows.Count > MaxComparisons)
            unavailable = "These tables have too many rows for the in-depth view.";
        if (unavailable is not null)
            return trace with { ComparisonsUnavailable = unavailable };

        var pairs = await _queries.RunAsync(database, shape.ComparisonsSql(orderings), restricted: true, ct);
        var comparisons = trace.Sources[0].Rows.Select(_ => new bool[trace.Sources[1].Rows.Count]).ToArray();
        foreach (var pair in pairs.Rows)
            comparisons[Convert.ToInt32(pair[0]) - 1][Convert.ToInt32(pair[1]) - 1] = Convert.ToInt32(pair[2]) != 0;
        return trace with { Comparisons = comparisons };
    }

    /// <summary>Orders rows by the source rows they came from, with rows that have no partner in a table last, the way a nested loop finds them.</summary>
    private static int ComparePaths(int[] a, int[] b)
    {
        for (var k = 0; k < a.Length; k++)
        {
            var x = a[k] == QueryTrace.NoMatch ? int.MaxValue : a[k];
            var y = b[k] == QueryTrace.NoMatch ? int.MaxValue : b[k];
            if (x != y)
                return x.CompareTo(y);
        }

        return 0;
    }

    /// <param name="Group">The row's group, or -1 if WHERE removed it or the query doesn't group.</param>
    private sealed record PipelineRow(int Arrival, IReadOnlyList<string> Cells, int[] Path, bool[] Conditions, int Group)
    {
        public bool Passes => Conditions.All(c => c);
    }
}
