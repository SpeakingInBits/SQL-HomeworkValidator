using System.Text;
using System.Text.RegularExpressions;
using HomeworkValidator.Core.Content;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HomeworkValidator.Core.Sql;

/// <summary>What the validator found in a student's script before running anything.</summary>
/// <param name="Batches">
/// The SQL to run, one entry per GO batch, each padded with blank lines so SQL Server's error line numbers
/// match the student's file. Empty when the script can't be run.
/// </param>
public sealed record ScriptAnalysis(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notes,
    IReadOnlySet<SqlFeature> Features,
    IReadOnlyList<string> Batches)
{
    /// <summary>True when everything in a DDL script was skipped (e.g. only CREATE DATABASE): nothing runs, but rules still apply.</summary>
    public bool NothingToRun { get; init; }

    public bool IsRunnable => Errors.Count == 0 && (Batches.Count > 0 || NothingToRun);
}

/// <summary>Parses T-SQL with ScriptDom, enforces which statements a question kind allows, and detects features for rules.</summary>
public static class ScriptAnalyzer
{
    /// <param name="database">The question's database. DDL scripts may create, drop or USE it; those statements are skipped.</param>
    public static ScriptAnalysis Analyze(string sql, QuestionKind kind, string? database = null) => kind switch
    {
        QuestionKind.Dml => AnalyzeScript(sql, ddl: false, database),
        QuestionKind.Ddl => AnalyzeScript(sql, ddl: true, database),
        _ => AnalyzeSelect(sql),
    };

    /// <summary>
    /// Analyzes a script for a SELECT question: exactly one SELECT statement, no SELECT INTO,
    /// and no database or server names (those would reach outside the validator's databases).
    /// USE statements are dropped so the query runs against the validator's copy.
    /// </summary>
    public static ScriptAnalysis AnalyzeSelect(string sql)
    {
        if (Parse(sql) is not { } script)
            return SyntaxErrors(sql);

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        var problems = new List<string>();
        var notes = new List<string>();
        var selects = new List<SelectStatement>();

        foreach (var statement in statements)
        {
            switch (statement)
            {
                case UseStatement use:
                    notes.Add(UseNote(use));
                    break;
                case SelectStatement { Into: not null }:
                    problems.Add($"Line {statement.StartLine}: SELECT INTO creates a table. This question only needs a SELECT query.");
                    break;
                case SelectStatement select:
                    selects.Add(select);
                    break;
                default:
                    problems.Add($"Line {statement.StartLine}: {Describe(statement)} isn't allowed here. This question only needs a SELECT query.");
                    break;
            }
        }

        if (selects.Count == 0 && problems.Count == 0)
            problems.Add("No SELECT statement found.");
        else if (selects.Count > 1)
            problems.Add($"Found {selects.Count} SELECT statements (lines {string.Join(", ", selects.Select(s => s.StartLine))}). Answer this question with a single query.");

        var visitor = new FeatureVisitor();
        foreach (var select in selects)
            select.Accept(visitor);
        problems.AddRange(QualifiedNameProblems(sql, visitor));

        if (problems.Count > 0)
            return new ScriptAnalysis(problems, notes, visitor.Features, []);

        var only = selects[0];
        return new ScriptAnalysis([], notes, visitor.Features, [Pad(only.StartLine) + Text(sql, only)]);
    }

    /// <summary>
    /// Analyzes a script for a DML question: INSERT, UPDATE, DELETE, SELECT (including INTO), DROP TABLE and
    /// the control flow and transaction statements that usually come with them. Anything that could reach
    /// outside the validator's database (EXEC, database statements, database names) is refused.
    /// USE statements are blanked out so the script runs against the validator's copy.
    /// </summary>
    public static ScriptAnalysis AnalyzeDml(string sql) => AnalyzeScript(sql, ddl: false, database: null);

    /// <summary>
    /// Analyzes a script for a DDL question: everything a DML script may do, plus creating, altering and dropping
    /// tables, indexes, views, procedures and functions.
    /// </summary>
    /// <remarks>
    /// Database-level statements are never run. CREATE, ALTER or DROP DATABASE for the question's own database
    /// (including an IF around them, such as <c>IF DB_ID('Membership') IS NOT NULL DROP DATABASE Membership</c>)
    /// and every USE are blanked out with a note; the rest of the script runs in the validator's copy of that database.
    /// A database statement naming any other database is refused.
    /// </remarks>
    public static ScriptAnalysis AnalyzeDdl(string sql, string database) => AnalyzeScript(sql, ddl: true, database);

    private static ScriptAnalysis AnalyzeScript(string sql, bool ddl, string? database)
    {
        if (Parse(sql) is not { } script)
            return SyntaxErrors(sql);

        var context = new ScriptContext(sql, ddl, database);
        foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            context.Check(statement);

        var visitor = new FeatureVisitor();
        script.Accept(visitor);
        var features = visitor.Features;
        if (context.CreatesDatabase)
            features.Add(SqlFeature.CreateDatabase);

        var problems = context.Problems.Concat(QualifiedNameProblems(sql, visitor)).ToList();
        var toRun = script.Batches.Where(b => b.Statements.Any(s => !context.Skipped.Contains(s))).ToList();
        if (toRun.Count == 0 && context.Skipped.Count == 0 && problems.Count == 0)
            problems.Add("No SQL statements found.");

        if (problems.Count > 0)
            return new ScriptAnalysis(problems, context.Notes, features, []);

        var batches = toRun.Select(b => Pad(b.StartLine) + Blank(Text(sql, b), b.StartOffset, context.Skipped)).ToList();
        // A DDL script may consist only of skipped statements, e.g. just CREATE DATABASE Membership.
        return new ScriptAnalysis([], context.Notes, features, batches) { NothingToRun = batches.Count == 0 };
    }

    /// <summary>Walks a script's statements, deciding which are allowed, which are skipped and which are refused.</summary>
    private sealed class ScriptContext(string sql, bool ddl, string? database)
    {
        public List<string> Problems { get; } = [];
        public List<string> Notes { get; } = [];
        public List<TSqlStatement> Skipped { get; } = [];
        public bool CreatesDatabase { get; private set; }

        public void Check(TSqlStatement statement)
        {
            switch (statement)
            {
                case UseStatement use:
                    Skipped.Add(use);
                    Notes.Add(UseNote(use));
                    break;
                case CreateDatabaseStatement create when ddl:
                    if (IsOwnDatabase(create.DatabaseName))
                    {
                        CreatesDatabase = true;
                        Skip(create);
                    }
                    else
                    {
                        WrongDatabase(create, create.DatabaseName.Value);
                    }
                    break;
                case DropDatabaseStatement drop when ddl:
                    if (drop.Databases.All(IsOwnDatabase))
                        Skip(drop);
                    else
                        WrongDatabase(drop, string.Join(", ", drop.Databases.Select(d => d.Value)));
                    break;
                case AlterDatabaseStatement alter when ddl:
                    if (alter.DatabaseName is not null && IsOwnDatabase(alter.DatabaseName))
                        Skip(alter);
                    else
                        WrongDatabase(alter, alter.DatabaseName?.Value ?? "the current database");
                    break;
                case IfStatement ifStatement:
                    Check(ifStatement.ThenStatement);
                    if (ifStatement.ElseStatement is not null)
                        Check(ifStatement.ElseStatement);
                    // IF DB_ID('Membership') IS NOT NULL DROP DATABASE Membership: skip the IF along with what it guards.
                    if (IsSkipped(ifStatement.ThenStatement) && (ifStatement.ElseStatement is null || IsSkipped(ifStatement.ElseStatement)))
                        Skipped.Add(ifStatement);
                    break;
                case BeginEndBlockStatement block:
                    foreach (var inner in block.StatementList.Statements)
                        Check(inner);
                    if (block.StatementList.Statements.All(IsSkipped))
                        Skipped.Add(block);
                    break;
                case var _ when IsDmlStatement(statement) || (ddl && IsDdlStatement(statement)):
                    break;
                default:
                    Problems.Add($"Line {statement.StartLine}: {Describe(statement)} isn't allowed here. " +
                                 (ddl
                                     ? "Use CREATE, ALTER and DROP TABLE, CREATE INDEX, and INSERT, UPDATE and DELETE statements."
                                     : "Use INSERT, UPDATE, DELETE, SELECT and DROP TABLE statements."));
                    break;
            }
        }

        private bool IsSkipped(TSqlStatement statement) => Skipped.Contains(statement);

        private bool IsOwnDatabase(Identifier name) =>
            database is not null && name.Value.Equals(database, StringComparison.OrdinalIgnoreCase);

        private void Skip(TSqlStatement statement)
        {
            Skipped.Add(statement);
            var text = Text(sql, statement).ReplaceLineEndings(" ").Trim().TrimEnd(';');
            Notes.Add($"Skipped '{text}': the validator never creates or drops databases. " +
                      $"It runs the rest of your script in its own copy of the {database} database.");
        }

        private void WrongDatabase(TSqlStatement statement, string name) =>
            Problems.Add($"Line {statement.StartLine}: this script works on database {name}, but this question uses the {database} database.");

        private static bool IsDmlStatement(TSqlStatement statement) => statement is
            InsertStatement or UpdateStatement or DeleteStatement or MergeStatement or SelectStatement
            or DropTableStatement or TruncateTableStatement or CreateTableStatement
            or DeclareVariableStatement or SetVariableStatement or PrintStatement
            or PredicateSetStatement or SetIdentityInsertStatement
            or BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement;

        // ALTER TABLE comes in many shapes (ADD, ALTER COLUMN, DROP, CHECK CONSTRAINT...), each its own ScriptDom class.
        private static bool IsDdlStatement(TSqlStatement statement) =>
            statement.GetType().Name.StartsWith("AlterTable", StringComparison.Ordinal)
            || statement is CreateIndexStatement or DropIndexStatement or AlterIndexStatement
                or CreateViewStatement or AlterViewStatement or CreateOrAlterViewStatement or DropViewStatement
                or CreateProcedureStatement or AlterProcedureStatement or CreateOrAlterProcedureStatement or DropProcedureStatement
                or CreateFunctionStatement or AlterFunctionStatement or CreateOrAlterFunctionStatement or DropFunctionStatement;
    }

    /// <summary>Returns rule violations for the detected features.</summary>
    public static IReadOnlyList<string> CheckRules(IReadOnlySet<SqlFeature> features, QuestionRules rules)
    {
        var violations = new List<string>();
        violations.AddRange(rules.Require.Where(f => !features.Contains(f)).Select(f => $"This question requires {Explain(f)}."));
        violations.AddRange(rules.Forbid.Where(features.Contains).Select(f => $"This question doesn't allow {Explain(f)}."));
        return violations;
    }

    public static string Explain(SqlFeature feature) => feature switch
    {
        SqlFeature.From => "a FROM clause",
        SqlFeature.Where => "a WHERE clause",
        SqlFeature.Join => "a join",
        SqlFeature.Subquery => "a subquery",
        SqlFeature.Cte => "a common table expression (WITH ...)",
        SqlFeature.OrderBy => "an ORDER BY clause",
        SqlFeature.GroupBy => "a GROUP BY clause",
        SqlFeature.Having => "a HAVING clause",
        SqlFeature.Distinct => "DISTINCT",
        SqlFeature.Top => "TOP",
        SqlFeature.SelectStar => "SELECT *",
        SqlFeature.Union => "UNION, INTERSECT or EXCEPT",
        SqlFeature.AliasWithoutAs => "column aliases without the AS keyword (write 'expression AS Alias')",
        SqlFeature.SelectInto => "SELECT INTO",
        SqlFeature.InsertWithoutColumnList => "an INSERT without a column list (list the columns: INSERT INTO Table (Column1, Column2) ...)",
        SqlFeature.CreateDatabase => "a CREATE DATABASE statement for this question's database",
        _ => feature.ToString(),
    };

    private static TSqlScript? Parse(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        return errors.Count == 0 ? (TSqlScript)fragment : null;
    }

    private static ScriptAnalysis SyntaxErrors(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        parser.Parse(new StringReader(sql), out var errors);
        var messages = errors.Select(e => $"Syntax error on line {e.Line}, column {e.Column}: {e.Message}").ToList();
        return new ScriptAnalysis(messages, [], new HashSet<SqlFeature>(), []);
    }

    private static string UseNote(UseStatement use) =>
        $"Ignored 'USE {use.DatabaseName.Value}': the validator runs your SQL against its own copy of the database.";

    private static IEnumerable<string> QualifiedNameProblems(string sql, FeatureVisitor visitor) =>
        visitor.QualifiedNames.Select(n =>
            $"Line {n.StartLine}: remove the database name from '{Text(sql, n)}'. The validator runs your SQL against its own copy of the database.");

    private static string Describe(TSqlStatement statement)
    {
        // InsertStatement -> "an INSERT statement", CreateTableStatement -> "a CREATE TABLE statement".
        var name = statement.GetType().Name;
        if (name.EndsWith("Statement", StringComparison.Ordinal))
            name = name[..^"Statement".Length];
        var words = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToUpperInvariant();
        var article = "AEIOU".Contains(words[0]) ? "an" : "a";
        return $"{article} {words} statement";
    }

    private static string Text(string sql, TSqlFragment fragment) =>
        sql.Substring(fragment.StartOffset, fragment.FragmentLength);

    private static string Pad(int startLine) => new('\n', startLine - 1);

    /// <summary>Replaces skipped statements (USE, database statements) inside a batch with spaces, keeping line breaks so line numbers don't move.</summary>
    private static string Blank(string batch, int batchOffset, IEnumerable<TSqlStatement> statements)
    {
        var text = new StringBuilder(batch);
        foreach (var statement in statements.Where(s => s.StartOffset >= batchOffset && s.StartOffset < batchOffset + batch.Length))
        {
            for (var i = statement.StartOffset - batchOffset; i < statement.StartOffset - batchOffset + statement.FragmentLength; i++)
            {
                if (text[i] is not ('\r' or '\n'))
                    text[i] = ' ';
            }
        }

        return text.ToString();
    }

    private sealed class FeatureVisitor : TSqlFragmentVisitor
    {
        public HashSet<SqlFeature> Features { get; } = [];
        public List<SchemaObjectName> QualifiedNames { get; } = [];

        public override void Visit(SelectStatement node)
        {
            if (node.QueryExpression?.OrderByClause is not null)
                Features.Add(SqlFeature.OrderBy);
            if (node.WithCtesAndXmlNamespaces?.CommonTableExpressions.Count > 0)
                Features.Add(SqlFeature.Cte);
            if (node.Into is not null)
                Features.Add(SqlFeature.SelectInto);
        }

        public override void Visit(QuerySpecification node)
        {
            AddFrom(node.FromClause);
            if (node.WhereClause is not null)
                Features.Add(SqlFeature.Where);
            if (node.GroupByClause is not null)
                Features.Add(SqlFeature.GroupBy);
            if (node.HavingClause is not null)
                Features.Add(SqlFeature.Having);
            if (node.UniqueRowFilter == UniqueRowFilter.Distinct)
                Features.Add(SqlFeature.Distinct);
            if (node.TopRowFilter is not null)
                Features.Add(SqlFeature.Top);
        }

        public override void Visit(UpdateSpecification node)
        {
            AddFrom(node.FromClause);
            if (node.WhereClause is not null)
                Features.Add(SqlFeature.Where);
        }

        public override void Visit(DeleteSpecification node)
        {
            AddFrom(node.FromClause);
            if (node.WhereClause is not null)
                Features.Add(SqlFeature.Where);
        }

        public override void Visit(InsertSpecification node)
        {
            if (node.Columns.Count == 0)
                Features.Add(SqlFeature.InsertWithoutColumnList);
        }

        public override void Visit(QualifiedJoin node) => Features.Add(SqlFeature.Join);
        public override void Visit(UnqualifiedJoin node) => Features.Add(SqlFeature.Join);
        public override void Visit(ScalarSubquery node) => Features.Add(SqlFeature.Subquery);
        public override void Visit(QueryDerivedTable node) => Features.Add(SqlFeature.Subquery);
        public override void Visit(BinaryQueryExpression node) => Features.Add(SqlFeature.Union);
        public override void Visit(SelectStarExpression node) => Features.Add(SqlFeature.SelectStar);

        public override void Visit(SelectScalarExpression node)
        {
            if (node.ColumnName is null)
                return;

            // "Alias = expression" puts the alias first.
            if (node.ColumnName.FirstTokenIndex < node.Expression.FirstTokenIndex)
            {
                Features.Add(SqlFeature.AliasWithoutAs);
                return;
            }

            var tokens = node.ScriptTokenStream;
            var hasAs = false;
            for (var i = node.Expression.LastTokenIndex + 1; i < node.ColumnName.FirstTokenIndex; i++)
                hasAs |= tokens[i].TokenType == TSqlTokenType.As;
            if (!hasAs)
                Features.Add(SqlFeature.AliasWithoutAs);
        }

        public override void Visit(SchemaObjectName node)
        {
            if (node.DatabaseIdentifier is not null || node.ServerIdentifier is not null)
                QualifiedNames.Add(node);
        }

        private void AddFrom(FromClause? from)
        {
            if (from is null)
                return;
            Features.Add(SqlFeature.From);
            // FROM a, b is an old-style join.
            if (from.TableReferences.Count > 1)
                Features.Add(SqlFeature.Join);
        }
    }
}
