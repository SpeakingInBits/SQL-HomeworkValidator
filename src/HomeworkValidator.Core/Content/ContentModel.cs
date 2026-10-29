using System.Text.RegularExpressions;

namespace HomeworkValidator.Core.Content;

/// <summary>A course database: one schema plus one or more datasets that fill it.</summary>
public sealed record CourseDatabase(string Name, string SchemaSql, IReadOnlyList<Dataset> Datasets)
{
    public Dataset GetDataset(string name) =>
        Datasets.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ContentException($"Database '{Name}' has no dataset named '{name}'.");
}

public sealed record Dataset(string Name, string Sql);

public sealed record Assignment(string Id, string Title, string Database, IReadOnlyList<Question> Questions);

public enum QuestionKind
{
    Select,
}

public sealed record Question(
    string Id,
    string AssignmentId,
    int Number,
    string Title,
    string Prompt,
    QuestionKind Kind,
    string Database,
    IReadOnlyList<string> Datasets,
    CompareOptions Compare,
    QuestionRules Rules,
    IReadOnlyDictionary<string, string> Hints,
    string SolutionSql,
    IReadOnlyList<Mutant> Mutants);

/// <summary>A known-wrong answer that must fail; used by <c>hv verify</c> to show whether the datasets are strong enough.</summary>
public sealed record Mutant(string Name, string Sql);

public enum ColumnMatch
{
    /// <summary>Columns must have the expected names; their order doesn't matter.</summary>
    ByName,

    /// <summary>Columns are matched by position; names are ignored.</summary>
    ByPosition,

    /// <summary>Only the number of rows is compared, for questions that don't say which columns to return.</summary>
    RowCount,
}

/// <summary>
/// One sort key from the question's ORDER BY requirement, named by result column.
/// When <see cref="KeyPattern"/> is set, only the part of the value it matches is the key
/// (for example the last name inside a "Last, First" column).
/// </summary>
public sealed record OrderKey(string Column, Regex? KeyPattern);

public sealed record CompareOptions(ColumnMatch Columns, IReadOnlyList<OrderKey> Order, int? Decimals)
{
    public static CompareOptions Default { get; } = new(ColumnMatch.ByName, [], null);

    public bool Ordered => Order.Count > 0;
}

public sealed record QuestionRules(IReadOnlySet<SqlFeature> Require, IReadOnlySet<SqlFeature> Forbid)
{
    public static QuestionRules None { get; } = new(new HashSet<SqlFeature>(), new HashSet<SqlFeature>());
}

/// <summary>Constructs a question can require or forbid. Detected with ScriptDom.</summary>
public enum SqlFeature
{
    From,
    Where,
    Join,
    Subquery,
    Cte,
    OrderBy,
    GroupBy,
    Having,
    Distinct,
    Top,
    SelectStar,
    Union,

    /// <summary>A column alias written without the AS keyword, e.g. <c>InvoiceTotal Total</c> or <c>Total = InvoiceTotal</c>.</summary>
    AliasWithoutAs,
}

public sealed class ContentException(string message) : Exception(message);
