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

        ResultTable pipeline;
        try
        {
            pipeline = await _queries.RunAsync(database, shape.PipelineSql(orderings), restricted: true, ct);
        }
        catch (QueryFailedException ex)
        {
            // It can fail where the real query doesn't, as the select list now also runs on rows WHERE would have removed.
            throw new QueryFailedException($"The visualizer couldn't follow this query. {ex.Message}");
        }

        return new TraceOutcome(await BuildAsync(database, shape, orderings, pipeline, actual.Rows.Count, ct), []);
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

    private async Task<QueryTrace> BuildAsync(
        string database, QueryShape shape, List<string> orderings, ResultTable pipeline, int actualRows, CancellationToken ct)
    {
        var sourceCount = shape.Sources.Count;
        var conditionCount = shape.Conditions.Count;
        var firstKey = pipeline.Columns.Count - conditionCount - sourceCount;
        var extraCount = shape.ExtraColumns.Count;
        var selected = firstKey - extraCount;

        // SELECT * also picks up the row numbers, which aren't part of the query's result.
        var shown = Enumerable.Range(0, selected).Where(c => pipeline.Columns[c] != QueryShape.RowNumberColumn).ToList();
        var columns = shown.Select(c => pipeline.Columns[c].Length > 0 ? pipeline.Columns[c] : "(no name)").Concat(shape.ExtraColumns).ToList();
        shown.AddRange(Enumerable.Range(selected, extraCount));

        // Rows arrive in ORDER BY order, which is their position in the sorted table.
        var display = ResultComparer.Rows(pipeline, pipeline.Rows.Count).Rows;
        var arrived = pipeline.Rows.Select((row, i) => new PipelineRow(
            i,
            shown.Select(c => display[i][c]).ToList(),
            Enumerable.Range(0, sourceCount).Select(k => row[firstKey + k] is { } number ? Convert.ToInt32(number) : QueryTrace.NoMatch).ToArray(),
            Enumerable.Range(0, conditionCount).Select(c => Convert.ToInt32(row[firstKey + sourceCount + c]) != 0).ToArray())).ToList();

        // Too many rows to watch: keep the ones that pass WHERE, as a comma join has very few of those among all the pairs.
        var total = arrived.Count;
        var rows = arrived.OrderBy(r => r.Conditions.All(c => c) ? 0 : 1).ThenBy(r => r.Path, Comparer<int[]>.Create(ComparePaths)).Take(MaxRows)
            .OrderBy(r => r.Path, Comparer<int[]>.Create(ComparePaths)).ToList();
        var rowOf = rows.Select((r, i) => (r.Arrival, i)).ToDictionary(x => x.Arrival, x => x.i);

        var filter = shape.Where is { } where
            ? new TraceFilter(where, shape.Conditions,
                Enumerable.Range(0, conditionCount).Select(c => (IReadOnlyList<bool>)rows.Select(r => r.Conditions[c]).ToList()).ToList(),
                rows.Select(r => r.Conditions.All(c => c)).ToList())
            : null;

        var sort = shape.SortKeys.Count > 0
            ? new TraceSort(shape.SortKeys, arrived.Where(r => rowOf.ContainsKey(r.Arrival) && r.Conditions.All(c => c)).Select(r => rowOf[r.Arrival]).ToList())
            : null;

        var sources = new List<TraceSource>();
        var paths = rows.Select(r => r.Path).ToList();
        var sourceTruncated = false;
        if (shape.Joins.Count > 0)
        {
            var positions = new List<Dictionary<int, int>>();
            for (var k = 0; k < sourceCount; k++)
            {
                var (source, position, truncated) = await ReadSourceAsync(database, shape, k, orderings[k], rows, ct);
                sources.Add(source);
                positions.Add(position);
                sourceTruncated |= truncated;
            }

            paths = paths.Select(p => p.Select((number, k) => number == QueryTrace.NoMatch ? QueryTrace.NoMatch : positions[k][number]).ToArray()).ToList();
        }

        var trace = new QueryTrace(
            shape.Sources[0].Table, sources, Joins(shape, sources), columns, shown.Count - extraCount,
            rows.Select(r => (IReadOnlyList<string>)r.Cells).ToList(), paths, filter, sort, [],
            total > rows.Count || sourceTruncated
                ? $"This query reads {total:N0} rows, which is too many to watch, so this shows {rows.Count:N0} of them."
                : null);

        trace = trace with { Cuts = Cuts(shape, trace, actualRows) };
        return shape.Joins.Count > 0
            ? await WithComparisonsAsync(database, shape, orderings, trace, total > rows.Count || sourceTruncated, ct)
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
                keeps[i] = seen.Add(string.Join('\u001F', trace.Rows[result[i]].Take(trace.ResultColumns)));
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

    private sealed record PipelineRow(int Arrival, IReadOnlyList<string> Cells, int[] Path, bool[] Conditions);
}
