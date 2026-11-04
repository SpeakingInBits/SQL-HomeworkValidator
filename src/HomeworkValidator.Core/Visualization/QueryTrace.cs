namespace HomeworkValidator.Core.Visualization;

/// <summary>A table in the FROM clause of a query with joins, as it was read from the database.</summary>
/// <param name="Table">The table's name as written.</param>
/// <param name="Alias">Its alias, if it has one.</param>
public sealed record TraceSource(string Table, string? Alias, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>The table's alias when it needs one to tell it apart, as in a self-join.</summary>
    public string Label => Alias is null ? Table : $"{Table} ({Alias})";
}

/// <summary>One join, adding source <c>[index + 1]</c>.</summary>
/// <param name="Type">As written for students, e.g. LEFT JOIN. A comma between tables is ",".</param>
/// <param name="On">The ON condition; null for joins that don't have one.</param>
/// <param name="LeftSource">The earlier source the ON condition connects to. It's usually the previous one, but not always.</param>
public sealed record TraceJoin(string Type, string? On, int LeftSource);

/// <summary>The WHERE clause, tested on each row of <see cref="QueryTrace.Rows"/>.</summary>
/// <param name="Conditions">The top-level AND parts of <paramref name="Text"/>.</param>
/// <param name="ConditionResults">[condition][row]: whether the condition is true for the row.</param>
/// <param name="Passes">Per row: whether it passes the whole WHERE.</param>
public sealed record TraceFilter(string Text, IReadOnlyList<string> Conditions, IReadOnlyList<IReadOnlyList<bool>> ConditionResults, IReadOnlyList<bool> Passes);

/// <summary>The ORDER BY clause.</summary>
/// <param name="Keys">Each sort key as written.</param>
/// <param name="Order">Indexes into <see cref="QueryTrace.Rows"/> of the rows that passed WHERE, in sorted order.</param>
public sealed record TraceSort(IReadOnlyList<string> Keys, IReadOnlyList<int> Order);

/// <summary>A last step that removes rows from the end result: DISTINCT or TOP.</summary>
/// <param name="Label">Says what happens, for students.</param>
/// <param name="Keeps">Per row of the result before any cuts: whether it's still there after this step.</param>
public sealed record TraceCut(string Label, IReadOnlyList<bool> Keeps);

/// <summary>
/// What a query did to each row, as reported by SQL Server: where each row came from, which WHERE conditions it passed,
/// where it sorted to and whether it made the final result. <see cref="SceneBuilder"/> turns this into an animation.
/// </summary>
/// <param name="Title">The table name, for a query on one table.</param>
/// <param name="Sources">Only for queries with joins; one table's rows are simply <paramref name="Rows"/>.</param>
/// <param name="Joins">One per source after the first.</param>
/// <param name="Columns">Headers of <paramref name="Rows"/>: the query's own columns, then any others that WHERE and ORDER BY read.</param>
/// <param name="ResultColumns">How many of <paramref name="Columns"/> are the query's own, which are the first ones.</param>
/// <param name="Rows">Each row of the FROM clause before WHERE, in the order of the source tables.</param>
/// <param name="Paths">Per row: its row in each source, or <see cref="NoMatch"/> where an outer join found none.</param>
/// <param name="Note">Tells the student when only some of the rows are shown.</param>
public sealed record QueryTrace(
    string Title,
    IReadOnlyList<TraceSource> Sources,
    IReadOnlyList<TraceJoin> Joins,
    IReadOnlyList<string> Columns,
    int ResultColumns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<int[]> Paths,
    TraceFilter? Filter,
    TraceSort? Sort,
    IReadOnlyList<TraceCut> Cuts,
    string? Note = null)
{
    /// <summary>In <see cref="Paths"/>: the source had no matching row, so an outer join filled in NULLs.</summary>
    public const int NoMatch = -1;

    /// <summary>
    /// [left row][right row]: whether a single join's ON condition is true for the pair. Null when it isn't available,
    /// and then <see cref="ComparisonsUnavailable"/> says why.
    /// </summary>
    public bool[][]? Comparisons { get; init; }

    public string? ComparisonsUnavailable { get; init; }

    /// <summary>The rows of the end result before any cuts, as indexes into <see cref="Rows"/>.</summary>
    public IReadOnlyList<int> Result =>
        Sort?.Order ?? Enumerable.Range(0, Rows.Count).Where(i => Filter?.Passes[i] != false).ToList();
}
