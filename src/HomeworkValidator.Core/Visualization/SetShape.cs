using HomeworkValidator.Core.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HomeworkValidator.Core.Visualization;

/// <summary>One UNION, INTERSECT or EXCEPT in a query.</summary>
/// <param name="Operator">As written for students, e.g. UNION ALL.</param>
/// <param name="Left">The table it reads first: a branch, or an earlier step's result. See <see cref="SetShape.Branches"/>.</param>
/// <param name="Right">The table it reads second.</param>
/// <param name="Sql">The query up to and including this operator, so SQL Server can say what it returns.</param>
public sealed record SetShapeStep(string Operator, int Left, int Right, string Sql);

/// <summary>
/// The parts of a query that combines others with UNION, INTERSECT or EXCEPT, found with ScriptDom. Tables are numbered with the
/// branches first, then one per step for that step's result. Steps are in the order they run, so a table is always ready for the step that reads it.
/// </summary>
public sealed class SetShape
{
    private readonly List<string> _branches = [];
    private readonly List<SetShapeStep> _steps = [];
    private int _branchCount;

    private SetShape(string query) => Query = query;

    /// <summary>The student's query, ready to run.</summary>
    public string Query { get; }

    /// <summary>What can't be shown, if anything.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];

    /// <summary>The text of each query being combined, in the order they're written.</summary>
    public IReadOnlyList<string> Branches => _branches;

    public IReadOnlyList<SetShapeStep> Steps => _steps;

    /// <summary>Each item of the ORDER BY that sorts the combined result.</summary>
    public IReadOnlyList<string> SortKeys { get; private set; } = [];

    /// <summary>The query's structure in words, e.g. <c>Query 1 UNION Query 2</c>.</summary>
    public string Description { get; private set; } = "";

    /// <summary>Reads a query that combines others. Returns null for any other query, including ones with mistakes, which <see cref="QueryShape"/> reports.</summary>
    public static SetShape? Analyze(string sql)
    {
        var analysis = ScriptAnalyzer.AnalyzeSelect(sql);
        if (!analysis.IsRunnable)
            return null;

        var query = analysis.Batches[0];
        var select = ScriptAnalyzer.ParseSelect(query);
        if (select.WithCtesAndXmlNamespaces is not null)
            return null;

        var top = select.QueryExpression;
        while (top is QueryParenthesisExpression parens)
            top = parens.QueryExpression;
        if (top is not BinaryQueryExpression)
            return null;

        var outer = select.QueryExpression;
        if (outer.OffsetClause is not null || top.OffsetClause is not null)
            return Unsupported(query, "OFFSET and FETCH");
        if (outer.ForClause is not null || top.ForClause is not null)
            return Unsupported(query, "FOR XML and FOR JSON");

        var shape = new SetShape(query);
        shape._branchCount = CountBranches(top);

        shape.Description = shape.Walk(top).Description;

        var orderBy = outer.OrderByClause ?? top.OrderByClause;
        if (orderBy is not null)
            shape.SortKeys = orderBy.OrderByElements.Select(shape.Text).ToList();
        return shape;
    }

    private static SetShape Unsupported(string query, string feature) =>
        new(query) { Problems = [$"The visualizer can't show {feature} yet."] };

    private static int CountBranches(QueryExpression expression) => expression switch
    {
        QueryParenthesisExpression parens => CountBranches(parens.QueryExpression),
        BinaryQueryExpression binary => CountBranches(binary.FirstQueryExpression) + CountBranches(binary.SecondQueryExpression),
        _ => 1,
    };

    /// <summary>Records the expression's branches and steps; returns the table that holds its result.</summary>
    private Operand Walk(QueryExpression expression)
    {
        switch (expression)
        {
            case QueryParenthesisExpression parens:
                var inner = Walk(parens.QueryExpression);
                var description = parens.QueryExpression is BinaryQueryExpression ? $"({inner.Description})" : inner.Description;
                return new Operand(inner.Table, description, parens.StartOffset, End(parens));

            case BinaryQueryExpression binary:
                return WalkChain(binary);

            default:
                _branches.Add(Text(expression));
                return new Operand(_branches.Count - 1, $"Query {_branches.Count}", expression.StartOffset, End(expression));
        }
    }

    /// <summary>
    /// Runs the operators of a chain in SQL Server's order: INTERSECT before UNION and EXCEPT, and otherwise from left to right.
    /// ScriptDom's tree doesn't follow that, so the chain is read back in the order it's written and grouped here.
    /// </summary>
    private Operand WalkChain(BinaryQueryExpression chain)
    {
        var expressions = new List<QueryExpression>();
        var operators = new List<string>();
        Flatten(chain, expressions, operators);

        var operands = expressions.Select(Walk).ToList();

        void Combine(int position)
        {
            var (left, right, name) = (operands[position], operands[position + 1], operators[position]);
            _steps.Add(new SetShapeStep(name, left.Table, right.Table, Query[left.Start..right.End]));
            operands[position] = new Operand(_branchCount + _steps.Count - 1, $"{left.Description} {name} {right.Description}", left.Start, right.End);
            operands.RemoveAt(position + 1);
            operators.RemoveAt(position);
        }

        while (operators.IndexOf("INTERSECT") is var intersect and >= 0)
            Combine(intersect);
        while (operators.Count > 0)
            Combine(0);
        return operands[0];
    }

    /// <summary>Lists a chain's operands and operators in the order they're written. Parentheses keep what's inside them together.</summary>
    private static void Flatten(QueryExpression expression, List<QueryExpression> operands, List<string> operators)
    {
        if (expression is not BinaryQueryExpression binary)
        {
            operands.Add(expression);
            return;
        }

        Flatten(binary.FirstQueryExpression, operands, operators);
        operators.Add(Name(binary));
        Flatten(binary.SecondQueryExpression, operands, operators);
    }

    private static int End(TSqlFragment fragment) => fragment.StartOffset + fragment.FragmentLength;

    private static string Name(BinaryQueryExpression binary) => binary.BinaryQueryExpressionType switch
    {
        BinaryQueryExpressionType.Except => "EXCEPT",
        BinaryQueryExpressionType.Intersect => "INTERSECT",
        _ => binary.All ? "UNION ALL" : "UNION",
    };

    private string Text(TSqlFragment fragment) => Query.Substring(fragment.StartOffset, fragment.FragmentLength);

    /// <summary>A table a step reads, and where the query that makes it is written.</summary>
    private sealed record Operand(int Table, string Description, int Start, int End);
}
