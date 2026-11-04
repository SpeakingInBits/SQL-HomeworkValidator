using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Visualization;

/// <summary>A table in a set operation: a query's rows, or what a step produced.</summary>
public sealed record SetTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>What a set operation did with one row it read.</summary>
/// <param name="Slot">Its position in the step's result, or -1 if it didn't get there.</param>
/// <param name="Related">
/// UNION: the row in the result that made this one a duplicate. INTERSECT and EXCEPT: the rows of the second table that match it.
/// </param>
public sealed record SetRowFate(int Slot, IReadOnlyList<int> Related);

/// <summary>One UNION, INTERSECT or EXCEPT.</summary>
/// <param name="Operator">As written for students, e.g. UNION ALL.</param>
/// <param name="Left">The index in <see cref="SetTrace.Tables"/> of the table it reads first.</param>
/// <param name="Right">The index of the table it reads second.</param>
/// <param name="Result">The index of the table it produces.</param>
/// <param name="LeftRows">What happened to each row of the first table.</param>
/// <param name="RightRows">What happened to each row of the second table. Only UNION brings them across; for INTERSECT and EXCEPT it's empty.</param>
public sealed record SetStep(string Operator, int Left, int Right, int Result, IReadOnlyList<SetRowFate> LeftRows, IReadOnlyList<SetRowFate> RightRows)
{
    /// <summary>True when the second table is only looked up, not added to the result.</summary>
    public bool LooksUp => Operator is "INTERSECT" or "EXCEPT";
}

/// <summary>A query being combined, and its own animation if it can have one.</summary>
/// <param name="Trace">What the query did on its own.</param>
/// <param name="Problem">Why it has no animation of its own.</param>
public sealed record SetBranch(QueryTrace? Trace, string? Problem);

/// <summary>What a query made of others did, as reported by SQL Server. <see cref="SetSceneBuilder"/> turns it into animations.</summary>
/// <param name="Description">The query's structure in words.</param>
/// <param name="Tables">The queries' rows, then each step's result.</param>
/// <param name="Steps">The set operations, in the order they run.</param>
/// <param name="SortKeys">The ORDER BY; empty if there's none.</param>
/// <param name="SortOrder">Positions in the last step's result, in sorted order.</param>
/// <param name="Branches">One per query being combined.</param>
/// <param name="Truncated">True when a table had too many rows to show them all.</param>
public sealed record SetTrace(
    string Description,
    IReadOnlyList<SetTable> Tables,
    IReadOnlyList<SetStep> Steps,
    IReadOnlyList<string> SortKeys,
    IReadOnlyList<int> SortOrder,
    IReadOnlyList<SetBranch> Branches,
    bool Truncated)
{
    /// <summary>The rows the animation ends on.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Result
    {
        get
        {
            var last = Tables[Steps[^1].Result].Rows;
            return SortKeys.Count > 0 ? SortOrder.Select(i => last[i]).ToList() : last;
        }
    }
}

/// <summary>What came of tracing a query made of others: the trace, or why there isn't one.</summary>
public sealed record SetOutcome(SetTrace? Trace, IReadOnlyList<string> Problems);

/// <summary>
/// Finds out what UNION, INTERSECT and EXCEPT do with each row. SQL Server runs each query and each operator, and rows are
/// matched to what it returned, so what counts as a duplicate is up to the server. Everything runs as the read-only database user.
/// </summary>
public sealed class SetTracer(ServerConnection server)
{
    private readonly QueryRunner _queries = new(server) { MaxRows = 200_000 };
    private readonly QueryTracer _tracer = new(server);

    /// <exception cref="QueryFailedException">The student's query failed.</exception>
    public async Task<SetOutcome> TraceAsync(string database, SetShape shape, CancellationToken ct = default)
    {
        if (shape.Problems.Count > 0)
            return new SetOutcome(null, shape.Problems);

        // Their own query first, so a mistake in it is reported in their words, with its line number.
        var actual = await _queries.RunAsync(database, shape.Query, restricted: true, ct);

        var truncated = false;
        var tables = new List<SetTable>();
        for (var i = 0; i < shape.Branches.Count; i++)
        {
            var rows = await _queries.RunAsync(database, shape.Branches[i], restricted: true, ct);
            truncated |= rows.Rows.Count > QueryTracer.MaxRows;
            tables.Add(new SetTable($"Query {i + 1}", Columns(rows), Rows(rows)));
        }

        var steps = new List<SetStep>();
        foreach (var step in shape.Steps)
        {
            var result = await _queries.RunAsync(database, step.Sql, restricted: true, ct);
            var (combined, trace) = Combine(step, tables, AllRows(result), Columns(result), tables.Count);
            tables.Add(combined);
            steps.Add(trace);
        }

        var branches = new List<SetBranch>();
        foreach (var branch in shape.Branches)
            branches.Add(await TraceBranchAsync(database, branch, ct));

        var order = shape.SortKeys.Count > 0 ? SortOrder(tables[^1], AllRows(actual)) : [];
        return new SetOutcome(new SetTrace(shape.Description, tables, steps, shape.SortKeys, order, branches, truncated), []);
    }

    private async Task<SetBranch> TraceBranchAsync(string database, string sql, CancellationToken ct)
    {
        try
        {
            var outcome = await _tracer.TraceAsync(database, sql, ct);
            return new SetBranch(outcome.Trace, outcome.Problems.Count > 0 ? string.Join(" ", outcome.Problems) : null);
        }
        catch (QueryFailedException ex)
        {
            return new SetBranch(null, ex.Message);
        }
    }

    private static List<string> Columns(ResultTable table) => table.Columns.Select(c => c.Length > 0 ? c : "(no name)").ToList();

    /// <summary>The rows to show of a query. Only these are limited: what they combine into can't be bigger than they are by much.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> Rows(ResultTable table) => ResultComparer.Rows(table, QueryTracer.MaxRows).Rows;

    private static IReadOnlyList<IReadOnlyList<string>> AllRows(ResultTable table) => ResultComparer.Rows(table, table.Rows.Count).Rows;

    private static string Key(IEnumerable<string> cells) => string.Join('\u001F', cells);

    /// <summary>
    /// Works out where each row went by taking rows from what the server returned as they're read: a row that
    /// finds its copy waiting there made it, and a row that doesn't was left out.
    /// </summary>
    private static (SetTable Table, SetStep Step) Combine(SetShapeStep step, List<SetTable> tables, IReadOnlyList<IReadOnlyList<string>> result, List<string> columns, int index)
    {
        var (left, right) = (tables[step.Left], tables[step.Right]);

        var waiting = new Dictionary<string, Queue<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result)
        {
            var key = Key(row);
            if (!waiting.TryGetValue(key, out var copies))
                waiting[key] = copies = new Queue<IReadOnlyList<string>>();
            copies.Enqueue(row);
        }

        var rows = new List<IReadOnlyList<string>>();
        var firstSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        SetRowFate Read(IReadOnlyList<string> row, IReadOnlyList<int>? matches)
        {
            var key = Key(row);
            if (waiting.TryGetValue(key, out var copies) && copies.Count > 0)
            {
                rows.Add(copies.Dequeue());
                firstSlot.TryAdd(key, rows.Count - 1);
                return new SetRowFate(rows.Count - 1, matches ?? []);
            }

            return new SetRowFate(-1, matches ?? (firstSlot.TryGetValue(key, out var slot) ? [slot] : []));
        }

        var looksUp = step.Operator is "INTERSECT" or "EXCEPT";
        var rightByKey = right.Rows.Select((row, i) => (Key(row), i)).ToLookup(x => x.Item1, x => x.i, StringComparer.OrdinalIgnoreCase);

        var leftRows = left.Rows.Select(row => Read(row, looksUp ? rightByKey[Key(row)].ToList() : null)).ToList();
        var rightRows = looksUp ? [] : right.Rows.Select(row => Read(row, null)).ToList();

        // Anything the matching missed still belongs in the result, so the animation ends on what the query really returns.
        // If tables were cut off there can be many such rows, and they're left out with the rows they came from.
        rows.AddRange(waiting.Values.SelectMany(copies => copies).Take(Math.Max(0, QueryTracer.MaxRows - rows.Count)));

        return (new SetTable($"Result of {step.Operator}", columns, rows),
                new SetStep(step.Operator, step.Left, step.Right, index, leftRows, rightRows));
    }

    /// <summary>Positions in the table of the rows the real query returned, in the order it returned them.</summary>
    private static List<int> SortOrder(SetTable table, IReadOnlyList<IReadOnlyList<string>> sorted)
    {
        var positions = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var key = Key(table.Rows[i]);
            if (!positions.TryGetValue(key, out var slots))
                positions[key] = slots = new Queue<int>();
            slots.Enqueue(i);
        }

        var order = new List<int>();
        foreach (var row in sorted)
        {
            if (positions.TryGetValue(Key(row), out var slots) && slots.Count > 0)
                order.Add(slots.Dequeue());
        }

        return order;
    }
}
