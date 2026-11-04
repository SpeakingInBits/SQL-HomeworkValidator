using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Visualization;

/// <summary>A row of the table after a statement ran.</summary>
/// <param name="Cells">Its values, for <see cref="TraceChange.Columns"/>.</param>
/// <param name="Before">Its row of <see cref="TraceChange.Before"/>, or -1 if the statement added it.</param>
/// <param name="Changed">Positions in <see cref="TraceChange.Columns"/> that the statement changed.</param>
public sealed record ChangeRow(IReadOnlyList<string> Cells, int Before, IReadOnlyList<int> Changed);

/// <summary>What one INSERT, UPDATE or DELETE did to its table, as reported by SQL Server. <see cref="ChangeSceneBuilder"/> turns it into an animation.</summary>
/// <param name="Table">The table it changed.</param>
/// <param name="Statement">The whole statement on one line.</param>
/// <param name="Set">UPDATE: the assignments, without SET.</param>
/// <param name="Where">The WHERE clause without the keyword; null if there's none.</param>
/// <param name="Columns">The columns shown: all of them, or the ones that matter when the table is wide.</param>
/// <param name="Before">The table's rows before the statement.</param>
/// <param name="Filter">The WHERE clause, tested on <paramref name="Before"/>. Null when there's none, or when it depends on a FROM clause.</param>
/// <param name="Hits">Per row of <paramref name="Before"/>: whether the statement changed or deleted it.</param>
/// <param name="After">The table's rows afterwards.</param>
/// <param name="Filled">INSERT: the columns the student's column list left out, which SQL Server fills in.</param>
/// <param name="Note">Says what's left out when not all columns or rows are shown.</param>
public sealed record TraceChange(
    ChangeKind Kind,
    string Table,
    string Statement,
    string? Set,
    string? Where,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Before,
    TraceFilter? Filter,
    IReadOnlyList<bool> Hits,
    IReadOnlyList<ChangeRow> After,
    IReadOnlyList<string> Filled,
    string? Note);

/// <summary>What came of tracing a script.</summary>
/// <param name="Changes">One per statement that ran and can be shown.</param>
/// <param name="Problems">Why there's nothing to show: mistakes in the script, or that it has no statement to show.</param>
/// <param name="Notes">Things to tell the student along with the animation.</param>
/// <param name="EndedTransaction">The script's own COMMIT or ROLLBACK ended the transaction, so the database has to be rebuilt.</param>
public sealed record ChangeOutcome(IReadOnlyList<TraceChange> Changes, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes, bool EndedTransaction);

/// <summary>
/// Finds out what a student's INSERT, UPDATE and DELETE statements do, by running the script as a check would:
/// as the restricted database user, in a transaction that's rolled back, so the database stays as it was.
/// </summary>
public sealed class ChangeTracer(ServerConnection server)
{
    /// <summary>Tables with more columns than this show only the ones that matter, as they're too wide to follow.</summary>
    private const int MaxColumns = 6;

    private readonly ScriptRunner _scripts = new(server);

    /// <param name="setup">Trusted batches that set up the question's starting state.</param>
    /// <exception cref="QueryFailedException">The student's script failed.</exception>
    public async Task<ChangeOutcome> TraceAsync(string database, IReadOnlyList<string> setup, string sql, CancellationToken ct = default)
    {
        var shape = ChangeShape.Analyze(sql);
        if (shape.Problems.Count > 0)
            return new ChangeOutcome([], shape.Problems, [], false);
        if (shape.Changes.Count == 0)
            return new ChangeOutcome([], [string.Join(" ", ["The visualizer shows INSERT, UPDATE and DELETE statements, and there isn't one here it can show.", .. shape.Notes])], [], false);

        var run = await _scripts.ReadResultsAsync(database, setup, shape.Batches, ChangeShape.ResultPrefix, ct);

        var changes = new List<TraceChange>();
        for (var i = 0; i < shape.Changes.Count; i++)
        {
            var before = run.Results.FirstOrDefault(r => r.Columns[0] == ChangeShape.BeforeColumn(i));
            var output = run.Results.FirstOrDefault(r => r.Columns[0] == ChangeShape.OutputColumn(i));
            if (before is not null && output is not null)
                changes.Add(Build(shape.Changes[i], before, output));
        }

        return new ChangeOutcome(changes, [], shape.Notes, run.EndedTransaction);
    }

    private static TraceChange Build(ShapeChange change, ResultTable unordered, ResultTable output)
    {
        // The server may read a table through any index, so rows could come back in any order. The first column is usually its ID.
        var before = unordered with { Rows = unordered.Rows.OrderBy(r => r[1] as IComparable, Comparer<IComparable?>.Default).ToList() };

        var conditionCount = change.Conditions.Count;
        var columnCount = before.Columns.Count - 1 - conditionCount;
        var allColumns = before.Columns.Skip(1).Take(columnCount).ToList();
        var beforeRows = ResultComparer.Rows(before, before.Rows.Count).Rows.Select(r => r.Skip(1).Take(columnCount).ToList()).ToList();
        var outputRows = ResultComparer.Rows(output, output.Rows.Count).Rows.Select(r => r.Skip(1).ToList()).ToList();

        // OUTPUT doesn't say which row it was, so rows are matched by their values. Identical rows can't be told apart, but then it doesn't matter.
        var waiting = new Dictionary<string, Queue<int>>();
        for (var i = 0; i < beforeRows.Count; i++)
        {
            var key = RowKey(beforeRows[i]);
            if (!waiting.TryGetValue(key, out var rows))
                waiting[key] = rows = new Queue<int>();
            rows.Enqueue(i);
        }

        var hits = new bool[beforeRows.Count];
        var updated = new Dictionary<int, List<string>>();
        var inserted = new List<List<string>>();
        foreach (var row in outputRows)
        {
            if (change.Kind == ChangeKind.Insert)
            {
                inserted.Add(row);
                continue;
            }

            var old = row.Take(columnCount).ToList();
            if (!waiting.TryGetValue(RowKey(old), out var rows) || rows.Count == 0)
                continue;
            var index = rows.Dequeue();
            hits[index] = true;
            if (change.Kind == ChangeKind.Update)
                updated[index] = row.Skip(columnCount).ToList();
        }

        // Too many rows to watch: keep the ones it changed.
        var keep = Enumerable.Range(0, beforeRows.Count).OrderBy(i => hits[i] ? 0 : 1).ThenBy(i => i).Take(QueryTracer.MaxRows).Order().ToList();
        var added = inserted.Take(QueryTracer.MaxRows).ToList();

        var shown = ShownColumns(change, allColumns, updated, beforeRows);
        List<string> Shown(List<string> cells) => shown.Select(c => cells[c]).ToList();

        var after = new List<ChangeRow>();
        foreach (var (row, position) in keep.Select((row, position) => (row, position)))
        {
            if (change.Kind == ChangeKind.Delete && hits[row])
                continue;
            var changed = updated.TryGetValue(row, out var cells)
                ? Enumerable.Range(0, shown.Count).Where(c => beforeRows[row][shown[c]] != cells[shown[c]]).ToList()
                : [];
            after.Add(new ChangeRow(Shown(cells ?? beforeRows[row]), position, changed));
        }

        after.AddRange(added.Select(row => new ChangeRow(Shown(row), -1, [])));

        TraceFilter? filter = null;
        if (change.Where is { } where && conditionCount > 0)
        {
            bool Test(int row, int condition) => Convert.ToInt32(before.Rows[row][1 + columnCount + condition]) != 0;
            filter = new TraceFilter(where, change.Conditions,
                Enumerable.Range(0, conditionCount).Select(c => (IReadOnlyList<bool>)keep.Select(r => Test(r, c)).ToList()).ToList(),
                keep.Select(r => Enumerable.Range(0, conditionCount).All(c => Test(r, c))).ToList());
        }

        var filled = change.InsertColumns.Count == 0
            ? []
            : allColumns.Where(c => !change.InsertColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

        var notes = new List<string>();
        if (shown.Count < columnCount)
            notes.Add($"Showing {shown.Count} of the {columnCount} columns in {change.Table}: the first two, and the ones this statement reads or changes.");
        if (keep.Count < beforeRows.Count || added.Count < inserted.Count)
            notes.Add($"{change.Table} has too many rows to watch, so this shows {QueryTracer.MaxRows:N0} of them.");

        return new TraceChange(
            change.Kind, change.Table, change.Statement, change.Set, change.Where,
            shown.Select(c => allColumns[c]).ToList(),
            keep.Select(r => (IReadOnlyList<string>)Shown(beforeRows[r])).ToList(),
            filter,
            keep.Select(r => hits[r]).ToList(),
            after,
            filled,
            notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    /// <summary>All the columns if they fit. Otherwise the first two, which usually say which row it is, and the ones the statement is about.</summary>
    private static List<int> ShownColumns(ShapeChange change, List<string> columns, Dictionary<int, List<string>> updated, List<List<string>> before)
    {
        var all = Enumerable.Range(0, columns.Count).ToList();
        if (change.Kind == ChangeKind.Insert || columns.Count <= MaxColumns)
            return all;

        return all.Where(c => c < 2
                              || change.ReadColumns.Contains(columns[c], StringComparer.OrdinalIgnoreCase)
                              || updated.Any(u => u.Value[c] != before[u.Key][c]))
            .ToList();
    }

    private static string RowKey(IEnumerable<string> cells) => string.Join('\u001F', cells);
}
