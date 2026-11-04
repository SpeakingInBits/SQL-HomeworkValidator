using System.Text;
using System.Text.RegularExpressions;
using HomeworkValidator.Core.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HomeworkValidator.Core.Visualization;

public enum ChangeKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>An INSERT, UPDATE or DELETE in a student's script.</summary>
/// <param name="Table">The table it changes, as written.</param>
/// <param name="Statement">The whole statement on one line.</param>
/// <param name="Set">UPDATE: the assignments, without SET.</param>
/// <param name="Where">The WHERE clause without the keyword; null if there's none.</param>
/// <param name="Conditions">The WHERE clause's top-level AND parts, when each can be tested on the table's own rows. Empty for statements with a FROM clause.</param>
/// <param name="ReadColumns">The columns it assigns and the ones its WHERE clause reads.</param>
/// <param name="InsertColumns">INSERT: the column list; empty if there's none.</param>
public sealed record ShapeChange(
    ChangeKind Kind,
    string Table,
    string Statement,
    string? Set,
    string? Where,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> ReadColumns,
    IReadOnlyList<string> InsertColumns);

/// <summary>
/// The INSERT, UPDATE and DELETE statements of a script, found with ScriptDom, and a copy of the script that reports what they do:
/// each one reads its table first, and gets an OUTPUT clause so that SQL Server says exactly which rows it changed.
/// OUTPUT is what makes this work on tables without a primary key, such as the ones SELECT INTO creates.
/// </summary>
public sealed class ChangeShape
{
    /// <summary>Result sets from the traced script start with a column named this, then the statement's position.</summary>
    public const string ResultPrefix = "__hv_";

    private const string BeforePrefix = "__hv_before_", OutputPrefix = "__hv_out_";

    /// <summary>Why the script can't be run. Empty when it can be.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];

    /// <summary>Things worth telling the student, such as statements that run but aren't shown.</summary>
    public IReadOnlyList<string> Notes { get; private init; } = [];

    /// <summary>The statements that are shown, in the order they appear.</summary>
    public IReadOnlyList<ShapeChange> Changes { get; private init; } = [];

    /// <summary>The script's batches, with the extra reads and OUTPUT clauses added on the same lines so error line numbers don't move.</summary>
    public IReadOnlyList<string> Batches { get; private init; } = [];

    public static string BeforeColumn(int change) => $"{BeforePrefix}{change}";
    public static string OutputColumn(int change) => $"{OutputPrefix}{change}";

    public static ChangeShape Analyze(string sql)
    {
        var analysis = ScriptAnalyzer.AnalyzeDml(sql);
        if (!analysis.IsRunnable)
            return new ChangeShape { Problems = analysis.Errors };

        var changes = new List<ShapeChange>();
        var batches = new List<string>();
        var nested = false;
        var unshown = false;

        foreach (var batch in analysis.Batches)
        {
            var script = ScriptAnalyzer.ParseScript(batch);
            var edits = new List<(int Offset, string Text)>();

            foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            {
                if (statement is InsertStatement or UpdateStatement or DeleteStatement)
                {
                    if (Read(batch, statement, changes.Count) is { } read)
                    {
                        changes.Add(read.Change);
                        edits.AddRange(read.Edits);
                    }
                    else
                    {
                        unshown = true;
                    }
                }
                else
                {
                    nested |= ChangeFinder.HasChange(statement);
                }
            }

            var text = new StringBuilder(batch);
            foreach (var (offset, insert) in edits.OrderByDescending(e => e.Offset))
                text.Insert(offset, insert);
            batches.Add(text.ToString());
        }

        var notes = new List<string>();
        if (nested)
            notes.Add("Statements inside IF, WHILE and BEGIN...END blocks run, but they aren't shown.");
        if (unshown)
            notes.Add("Statements with their own OUTPUT clause, or that change a table variable or a common table expression, run, but they aren't shown.");

        return new ChangeShape { Changes = changes, Batches = batches, Notes = notes };
    }

    /// <summary>Describes a statement and the text to add for it; null if it can't be shown.</summary>
    private static (ShapeChange Change, List<(int Offset, string Text)> Edits)? Read(string batch, TSqlStatement statement, int index)
    {
        string Text(TSqlFragment fragment) => batch.Substring(fragment.StartOffset, fragment.FragmentLength);
        int End(TSqlFragment fragment) => fragment.StartOffset + fragment.FragmentLength;

        ShapeChange change;
        int outputAt;
        string output;

        switch (statement)
        {
            case InsertStatement { InsertSpecification: { OutputClause: null, OutputIntoClause: null } insert }:
                if (Table(insert.Target, null, Text) is not { } inserting)
                    return null;
                change = new ShapeChange(ChangeKind.Insert, inserting, OneLine(Text(statement)), null, null, [], [],
                    insert.Columns.Select(c => ColumnName(c)).ToList());
                outputAt = insert.InsertSource.StartOffset;
                output = "inserted.*";
                break;

            case UpdateStatement { UpdateSpecification: { OutputClause: null, OutputIntoClause: null } update }:
                if (Table(update.Target, update.FromClause, Text) is not { } updating)
                    return null;
                var (first, last) = (update.SetClauses[0], update.SetClauses[^1]);
                var assigned = update.SetClauses.OfType<AssignmentSetClause>().Where(c => c.Column is not null).Select(c => ColumnName(c.Column));
                change = Filtered(ChangeKind.Update, updating, batch[first.StartOffset..End(last)], update.WhereClause, update.FromClause, assigned);
                outputAt = End(last);
                output = "deleted.*, inserted.*";
                break;

            case DeleteStatement { DeleteSpecification: { OutputClause: null, OutputIntoClause: null } delete }:
                if (Table(delete.Target, delete.FromClause, Text) is not { } deleting)
                    return null;
                change = Filtered(ChangeKind.Delete, deleting, null, delete.WhereClause, delete.FromClause, []);
                outputAt = End(delete.Target);
                output = "deleted.*";
                break;

            default:
                return null;
        }

        var tests = string.Concat(change.Conditions.Select(c => $", CASE WHEN ({c}) THEN 1 ELSE 0 END"));
        var before = $"SELECT {index} AS [{BeforeColumn(index)}], {change.Table}.*{tests} FROM {change.Table}; ";
        var outputClause = $" OUTPUT {index} AS [{OutputColumn(index)}], {output} ";
        return (change, [(statement.StartOffset, before), (outputAt, outputClause)]);

        ShapeChange Filtered(ChangeKind kind, string table, string? set, WhereClause? where, FromClause? from, IEnumerable<string> assigned)
        {
            var condition = where?.SearchCondition;
            var read = condition is null ? [] : QueryShape.ColumnVisitor.Find(condition).Select(c => c.Name);
            var conditions = condition is null || from is not null ? [] : QueryShape.SplitAnd(condition).Select(Text).ToList();
            return new ShapeChange(kind, table, OneLine(Text(statement)), set, condition is null ? null : Text(condition), conditions,
                assigned.Concat(read).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), []);
        }
    }

    private static string ColumnName(ColumnReferenceExpression column) => column.MultiPartIdentifier.Identifiers[^1].Value;

    /// <summary>The table a statement changes. With a FROM clause, the statement may name it by alias.</summary>
    private static string? Table(TableReference target, FromClause? from, Func<TSqlFragment, string> text)
    {
        if (target is not NamedTableReference named)
            return null;

        if (from is not null)
        {
            var tables = new TableFinder();
            from.Accept(tables);
            var name = named.SchemaObject.BaseIdentifier.Value;
            if (tables.Found.FirstOrDefault(t => t.Alias?.Value.Equals(name, StringComparison.OrdinalIgnoreCase) == true) is { } aliased)
                return text(aliased.SchemaObject);
        }

        return text(named.SchemaObject);
    }

    private static string OneLine(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private sealed class TableFinder : TSqlFragmentVisitor
    {
        public List<NamedTableReference> Found { get; } = [];

        public override void Visit(NamedTableReference node) => Found.Add(node);
    }

    /// <summary>Finds INSERT, UPDATE and DELETE statements inside another statement, such as an IF.</summary>
    private sealed class ChangeFinder : TSqlFragmentVisitor
    {
        private bool _found;

        public static bool HasChange(TSqlFragment fragment)
        {
            var finder = new ChangeFinder();
            fragment.Accept(finder);
            return finder._found;
        }

        public override void Visit(InsertStatement node) => _found = true;
        public override void Visit(UpdateStatement node) => _found = true;
        public override void Visit(DeleteStatement node) => _found = true;
    }
}
