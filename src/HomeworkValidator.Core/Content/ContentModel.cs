using System.Text.RegularExpressions;

namespace HomeworkValidator.Core.Content;

/// <summary>A course database: one schema plus one or more datasets that fill it.</summary>
public sealed record CourseDatabase(string Name, string SchemaSql, IReadOnlyList<Dataset> Datasets)
{
    public Dataset GetDataset(string name) =>
        Datasets.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ContentException($"Database '{Name}' has no dataset named '{name}'.");
}

/// <param name="Hint">Shown when a check fails on this dataset and the question has no hint of its own.</param>
public sealed record Dataset(string Name, string Sql, string? Hint = null);

public sealed record Assignment(string Id, string Title, string Database, IReadOnlyList<Question> Questions)
{
    /// <summary>
    /// The day the app starts offering "Show solution" for this assignment's questions, e.g. after the due date
    /// (<see cref="DateOnly.MinValue"/> for always); null to never offer it. Set with <c>showSolutions</c> in assignment.yaml.
    /// </summary>
    public DateOnly? SolutionsFrom { get; init; }

    public bool ShowsSolutions(DateOnly today) => SolutionsFrom is { } from && today >= from;
}

public enum QuestionKind
{
    /// <summary>A single query; its result set is compared.</summary>
    Select,

    /// <summary>A script that changes data (INSERT, UPDATE, DELETE, SELECT INTO, DROP TABLE); the resulting tables are compared.</summary>
    Dml,

    /// <summary>
    /// A script that changes the database's design (CREATE/ALTER TABLE, indexes, constraints). The resulting schema is compared,
    /// then the question's probes show whether constraints and defaults behave the same.
    /// </summary>
    Ddl,
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
    IReadOnlyList<NamedScript> Mutants)
{
    /// <summary>Shown under the prompt, e.g. which ID an earlier exercise's row gets in the validator's copy.</summary>
    public string? Note { get; init; }

    /// <summary>Other correct answers, for questions that can reasonably be read more than one way.</summary>
    public IReadOnlyList<NamedScript> Alternatives { get; init; } = [];

    /// <summary>DML and DDL: IDs of earlier questions whose reference solutions run first, e.g. the one that creates VendorCopy.</summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>DML and DDL: SQL that runs after <see cref="After"/> and before the answer (q&lt;n&gt;.setup.sql).</summary>
    public string? SetupSql { get; init; }

    /// <summary>
    /// DDL only: small scripts run against both the expected design and the student's, such as "insert a group with
    /// negative dues". Whether each succeeds, and what it returns, must match.
    /// </summary>
    public IReadOnlyList<NamedScript> Probes { get; init; } = [];

    /// <summary>True for questions where the student writes a script that changes the database, not a query.</summary>
    public bool IsScript => Kind != QuestionKind.Select;
}

/// <summary>A named script: a known-wrong answer (mutant) or an alternative correct answer.</summary>
public sealed record NamedScript(string Name, string Sql);

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

/// <param name="PresenceOnly">
/// DML only: "Table.Column" names whose values are only checked for being set or NULL, such as dates set with GETDATE(),
/// which differ between the student's run and the reference run.
/// </param>
public sealed record CompareOptions(ColumnMatch Columns, IReadOnlyList<OrderKey> Order, int? Decimals)
{
    public static CompareOptions Default { get; } = new(ColumnMatch.ByName, [], null);

    public IReadOnlyList<string> PresenceOnly { get; init; } = [];

    /// <summary>DDL only: varchar(50) and varchar(100) count as the same type, for designs that don't give lengths.</summary>
    public bool IgnoreLengths { get; init; }

    /// <summary>DDL only: don't compare which columns allow NULLs, for questions that leave it to the student.</summary>
    public bool IgnoreNullability { get; init; }

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

    /// <summary><c>SELECT ... INTO NewTable</c>.</summary>
    SelectInto,

    /// <summary>An INSERT that doesn't list its columns, e.g. <c>INSERT INTO Categories VALUES ('Brass')</c>.</summary>
    InsertWithoutColumnList,

    /// <summary><c>CREATE DATABASE</c> for the question's database. It's recognized but never run.</summary>
    CreateDatabase,
}

public sealed class ContentException(string message) : Exception(message);
