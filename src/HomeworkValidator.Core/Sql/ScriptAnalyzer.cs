using HomeworkValidator.Core.Content;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HomeworkValidator.Core.Sql;

/// <summary>What the validator found in a student's script before running anything.</summary>
public sealed record ScriptAnalysis(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notes,
    IReadOnlySet<SqlFeature> Features,
    string? ExecutableSql)
{
    public bool IsRunnable => Errors.Count == 0 && ExecutableSql is not null;
}

/// <summary>Parses T-SQL with ScriptDom, enforces which statements a question kind allows, and detects features for rules.</summary>
public static class ScriptAnalyzer
{
    /// <summary>
    /// Analyzes a script for a SELECT question: exactly one SELECT statement, no SELECT INTO,
    /// and no database or server names (those would reach outside the validator's databases).
    /// USE statements are dropped so the query runs against the validator's copy.
    /// </summary>
    public static ScriptAnalysis AnalyzeSelect(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errors = parseErrors
                .Select(e => $"Syntax error on line {e.Line}, column {e.Column}: {e.Message}")
                .ToList();
            return new ScriptAnalysis(errors, [], new HashSet<SqlFeature>(), null);
        }

        var script = (TSqlScript)fragment;
        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        var problems = new List<string>();
        var notes = new List<string>();
        var selects = new List<SelectStatement>();

        foreach (var statement in statements)
        {
            switch (statement)
            {
                case UseStatement use:
                    notes.Add($"Ignored 'USE {use.DatabaseName.Value}': the validator runs your query against its own copy of the database.");
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
        problems.AddRange(visitor.QualifiedNames.Select(n =>
            $"Line {n.StartLine}: remove the database name from '{Text(sql, n)}'. The validator runs your query against its own copy of the database."));

        if (problems.Count > 0)
            return new ScriptAnalysis(problems, notes, visitor.Features, null);

        var only = selects[0];
        // Pad with newlines so line numbers in SQL Server errors match the student's file.
        var executable = new string('\n', only.StartLine - 1) + Text(sql, only);
        return new ScriptAnalysis([], notes, visitor.Features, executable);
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
        _ => feature.ToString(),
    };

    private static string Describe(TSqlStatement statement)
    {
        // InsertStatement -> "an INSERT statement", CreateTableStatement -> "a CREATE TABLE statement".
        var name = statement.GetType().Name;
        if (name.EndsWith("Statement", StringComparison.Ordinal))
            name = name[..^"Statement".Length];
        var words = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToUpperInvariant();
        var article = "AEIOU".Contains(words[0]) ? "an" : "a";
        return $"{article} {words} statement";
    }

    private static string Text(string sql, TSqlFragment fragment) =>
        sql.Substring(fragment.StartOffset, fragment.FragmentLength);

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
        }

        public override void Visit(QuerySpecification node)
        {
            if (node.FromClause is not null)
            {
                Features.Add(SqlFeature.From);
                // FROM a, b is an old-style join.
                if (node.FromClause.TableReferences.Count > 1)
                    Features.Add(SqlFeature.Join);
            }
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
    }
}
