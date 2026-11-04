using System.Text;
using HomeworkValidator.Core.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace HomeworkValidator.Core.Visualization;

/// <summary>A table in the FROM clause. <see cref="Reference"/> is what the rest of the query calls it: its alias, or else its name.</summary>
public sealed record ShapeSource(string Table, string? Alias, string Reference)
{
    internal int Offset { get; init; }
    internal int Length { get; init; }
}

/// <summary>A column the query names, split into its table prefix (empty if there's none) and its name.</summary>
public sealed record ShapeColumn(string Prefix, string Name);

/// <summary>One join, adding the next table in <see cref="QueryShape.Sources"/>.</summary>
/// <param name="Type">As written for students, e.g. LEFT JOIN. A comma between tables is ",".</param>
/// <param name="On">The ON condition; null for joins that don't have one.</param>
/// <param name="OnColumns">The columns the ON condition reads, to work out which earlier table it connects to.</param>
public sealed record ShapeJoin(string Type, string? On, IReadOnlyList<ShapeColumn> OnColumns);

/// <summary>A value shown for each row before grouping: a group key, or what an aggregate reads.</summary>
public sealed record ShapeRowColumn(string Header, string Expression);

/// <summary>
/// The parts of a SELECT the visualizer shows, found with ScriptDom. It also writes the queries that ask SQL Server what
/// happens to each row: the same FROM, select list and ORDER BY as the student's query, so the server's own semantics
/// (outer joins, NULLs, grouping, sorting) decide everything and nothing here reimplements them.
/// </summary>
public sealed class QueryShape
{
    /// <summary>Name of the row number column added to each table. Rows are identified by it, as SQL Server has no rowid.</summary>
    public const string RowNumberColumn = "__hv_rn";

    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AVG", "CHECKSUM_AGG", "COUNT", "COUNT_BIG", "GROUPING", "GROUPING_ID", "MAX", "MIN", "STDEV", "STDEVP", "STRING_AGG", "SUM", "VAR", "VARP",
    };

    private string _from = "";
    private int _fromOffset;
    private string _selectList = "";
    private string _orderBy = "";
    private string _groupBy = "";

    private QueryShape(string query) => Query = query;

    /// <summary>Why the query can't be visualized: mistakes in it, or a feature the visualizer doesn't show. Empty when it can be.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];

    /// <summary>The student's query, ready to run.</summary>
    public string Query { get; }

    public IReadOnlyList<ShapeSource> Sources { get; private set; } = [];
    public IReadOnlyList<ShapeJoin> Joins { get; private set; } = [];

    /// <summary>The WHERE clause without the keyword; null if there's none.</summary>
    public string? Where { get; private set; }

    /// <summary>The WHERE clause split at its top-level ANDs, so each part can be shown on its own card.</summary>
    public IReadOnlyList<string> Conditions { get; private set; } = [];

    /// <summary>Each ORDER BY item as written.</summary>
    public IReadOnlyList<string> SortKeys { get; private set; } = [];

    public bool Distinct { get; private set; }

    /// <summary>The TOP clause as written, e.g. <c>TOP 5</c>; null if there's none.</summary>
    public string? Top { get; private set; }

    /// <summary>
    /// Columns the WHERE and ORDER BY read but the select list leaves out, as written. They're shown next to the row so
    /// students can see why it passes or where it sorts to. Not used by grouped queries, whose rows show <see cref="RowColumns"/>.
    /// </summary>
    public IReadOnlyList<string> ExtraColumns { get; private set; } = [];

    /// <summary>True when rows collapse into groups: GROUP BY, HAVING, or an aggregate over all the rows.</summary>
    public bool IsGrouped { get; private set; }

    /// <summary>True when the query has window functions, which work on groups of rows but keep every row.</summary>
    public bool IsWindowed { get; private set; }

    /// <summary>What rows are grouped by: GROUP BY's items, or the PARTITION BY of the query's first window function.</summary>
    public IReadOnlyList<string> GroupKeys { get; private set; } = [];

    /// <summary>The clause to show for the grouping step, e.g. <c>GROUP BY VendorState</c>; null if there's none to quote.</summary>
    public string? GroupClause { get; private set; }

    /// <summary>The ROLLUP or CUBE that adds summary rows, as students wrote it; null without one.</summary>
    public string? SummaryRows { get; private set; }

    /// <summary>The HAVING clause without the keyword; null if there's none.</summary>
    public string? Having { get; private set; }

    /// <summary>The HAVING clause split at its top-level ANDs.</summary>
    public IReadOnlyList<string> HavingConditions { get; private set; } = [];

    /// <summary>What to show for each row before it's grouped. The first <see cref="KeyColumns"/> are the group keys.</summary>
    public IReadOnlyList<ShapeRowColumn> RowColumns { get; private set; } = [];

    public int KeyColumns { get; private set; }

    /// <summary>Positions in the select list of its calculated columns, as opposed to plain columns.</summary>
    public IReadOnlyList<int> CalculatedColumns { get; private set; } = [];

    /// <summary>True when window functions partition differently. Only the first one's partitions are shown.</summary>
    public bool MixedPartitions { get; private set; }

    public static QueryShape Analyze(string sql)
    {
        var analysis = ScriptAnalyzer.AnalyzeSelect(sql);
        if (!analysis.IsRunnable)
            return new QueryShape(sql) { Problems = analysis.Errors };

        var query = analysis.Batches[0];
        var select = ScriptAnalyzer.ParseSelect(query);
        var shape = new QueryShape(query);
        var unsupported = shape.Read(select);
        return unsupported is null
            ? shape
            : new QueryShape(query)
            {
                Problems = [$"The visualizer can't show {unsupported} yet. It shows joins, WHERE, GROUP BY, HAVING, window functions, UNION, ORDER BY, DISTINCT and TOP."],
            };
    }

    /// <summary>Fills in the shape; returns what it can't show, if anything.</summary>
    private string? Read(SelectStatement select)
    {
        if (select.WithCtesAndXmlNamespaces is not null)
            return "common table expressions (WITH)";

        var expression = select.QueryExpression;
        while (expression is QueryParenthesisExpression parens)
            expression = parens.QueryExpression;
        if (expression is BinaryQueryExpression)
            return "UNION, INTERSECT or EXCEPT";
        if (expression is not QuerySpecification spec)
            return "this kind of query";

        if (spec.FromClause is null)
            return "a query without a FROM clause";
        if (select.QueryExpression.OffsetClause is not null || spec.OffsetClause is not null)
            return "OFFSET and FETCH";
        if (select.QueryExpression.ForClause is not null || spec.ForClause is not null)
            return "FOR XML and FOR JSON";
        if (spec.SelectElements.Any(e => e is SelectSetVariable))
            return "assigning to a variable";

        var orderBy = select.QueryExpression.OrderByClause ?? spec.OrderByClause;
        var functions = new FunctionVisitor();
        foreach (var element in spec.SelectElements)
            element.Accept(functions);
        orderBy?.Accept(functions);
        spec.HavingClause?.SearchCondition?.Accept(functions);

        IsGrouped = spec.GroupByClause is not null || spec.HavingClause is not null || functions.HasAggregate;
        IsWindowed = functions.Windows.Count > 0;
        if (IsGrouped && IsWindowed)
            return "window functions in a query with GROUP BY";

        var sources = new List<NamedTableReference>();
        var joins = new List<ShapeJoin>();
        for (var i = 0; i < spec.FromClause.TableReferences.Count; i++)
        {
            var firstJoin = joins.Count;
            if (Flatten(spec.FromClause.TableReferences[i], sources, joins) is { } problem)
                return problem;
            if (i > 0)
                joins.Insert(firstJoin, new ShapeJoin(",", null, []));
        }

        // Each table's row numbers are among the columns * expands to, which would move every column after the first table.
        if (sources.Count > 1 && spec.SelectElements.Any(e => e is SelectStarExpression) && orderBy?.OrderByElements.Any(e => e.Expression is IntegerLiteral) == true)
            return "ORDER BY with a column number after SELECT * on several tables";

        var keys = new List<ScalarExpression>();
        if (spec.GroupByClause is { } groupBy)
        {
            if (ReadGroupBy(groupBy, keys) is { } problem)
                return problem;
            _groupBy = Text(groupBy);
            GroupClause = _groupBy;
        }
        else if (IsWindowed)
        {
            var window = functions.Windows[0];
            keys.AddRange(window.Partitions);
            GroupClause = keys.Count > 0 ? $"PARTITION BY {string.Join(", ", keys.Select(Text))}" : null;
            MixedPartitions = functions.Windows.Select(w => string.Join(",", w.Partitions.Select(Text).Order(StringComparer.OrdinalIgnoreCase)).ToUpperInvariant()).Distinct().Count() > 1;
        }

        GroupKeys = keys.Select(Text).ToList();

        Sources = sources.Select(s => new ShapeSource(
            Text(s.SchemaObject), s.Alias?.Value, s.Alias?.Value ?? s.SchemaObject.BaseIdentifier.Value)
        {
            Offset = s.StartOffset,
            Length = s.FragmentLength,
        }).ToList();
        Joins = joins;

        _from = Text(spec.FromClause);
        // The clause starts with the FROM keyword; the offset is where its text starts in the query.
        _fromOffset = spec.FromClause.StartOffset;
        var first = spec.SelectElements[0];
        var last = spec.SelectElements[^1];
        _selectList = Query[first.StartOffset..(last.StartOffset + last.FragmentLength)];

        if (spec.WhereClause?.SearchCondition is { } where)
        {
            Where = Text(where);
            Conditions = SplitAnd(where).Select(Text).ToList();
        }

        if (spec.HavingClause?.SearchCondition is { } having)
        {
            Having = Text(having);
            HavingConditions = SplitAnd(having).Select(Text).ToList();
        }

        if (orderBy is not null)
        {
            _orderBy = Text(orderBy);
            SortKeys = orderBy.OrderByElements.Select(Text).ToList();
        }

        Distinct = spec.UniqueRowFilter == UniqueRowFilter.Distinct;
        Top = spec.TopRowFilter is { } top ? Text(top) : null;

        if (IsGrouped || IsWindowed)
        {
            RowColumns = FindRowColumns(spec, orderBy, keys, functions);
            CalculatedColumns = FindCalculatedColumns(spec);
        }
        else
        {
            ExtraColumns = FindExtraColumns(spec, orderBy);
        }

        return null;
    }

    /// <summary>Collects the keys of a GROUP BY; returns what it can't show, if anything.</summary>
    private string? ReadGroupBy(GroupByClause clause, List<ScalarExpression> keys)
    {
        if (clause.All)
            return "GROUP BY ALL";

        if (clause.GroupByOption is GroupByOption.Rollup)
            SummaryRows = "WITH ROLLUP";
        else if (clause.GroupByOption is GroupByOption.Cube)
            SummaryRows = "WITH CUBE";

        foreach (var item in clause.GroupingSpecifications)
        {
            switch (item)
            {
                case ExpressionGroupingSpecification plain:
                    keys.Add(plain.Expression);
                    break;
                case RollupGroupingSpecification rollup when rollup.Arguments.All(a => a is ExpressionGroupingSpecification):
                    SummaryRows = "ROLLUP";
                    keys.AddRange(rollup.Arguments.Cast<ExpressionGroupingSpecification>().Select(a => a.Expression));
                    break;
                case CubeGroupingSpecification cube when cube.Arguments.All(a => a is ExpressionGroupingSpecification):
                    SummaryRows = "CUBE";
                    keys.AddRange(cube.Arguments.Cast<ExpressionGroupingSpecification>().Select(a => a.Expression));
                    break;
                default:
                    return "GROUPING SETS";
            }
        }

        return null;
    }

    private string? Flatten(TableReference table, List<NamedTableReference> sources, List<ShapeJoin> joins)
    {
        switch (table)
        {
            case NamedTableReference named:
                sources.Add(named);
                return null;
            case QualifiedJoin join:
                return FlattenJoin(join.FirstTableReference, join.SecondTableReference, sources, joins,
                    new ShapeJoin(JoinType(join.QualifiedJoinType), Text(join.SearchCondition), ColumnVisitor.Find(join.SearchCondition)));
            case UnqualifiedJoin { UnqualifiedJoinType: UnqualifiedJoinType.CrossJoin } cross:
                return FlattenJoin(cross.FirstTableReference, cross.SecondTableReference, sources, joins, new ShapeJoin("CROSS JOIN", null, []));
            case UnqualifiedJoin:
                return "CROSS APPLY and OUTER APPLY";
            case QueryDerivedTable:
                return "a subquery in the FROM clause";
            default:
                return "this kind of table in the FROM clause";
        }
    }

    private string? FlattenJoin(TableReference left, TableReference right, List<NamedTableReference> sources, List<ShapeJoin> joins, ShapeJoin join)
    {
        if (Flatten(left, sources, joins) is { } problem)
            return problem;
        if (right is QueryDerivedTable)
            return "a subquery in the FROM clause";
        if (right is not NamedTableReference named)
            return "a join inside parentheses";
        sources.Add(named);
        joins.Add(join);
        return null;
    }

    private static string JoinType(QualifiedJoinType type) => type switch
    {
        QualifiedJoinType.LeftOuter => "LEFT JOIN",
        QualifiedJoinType.RightOuter => "RIGHT JOIN",
        QualifiedJoinType.FullOuter => "FULL JOIN",
        _ => "JOIN",
    };

    internal static IEnumerable<BooleanExpression> SplitAnd(BooleanExpression expression) =>
        expression is BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and
            ? SplitAnd(and.FirstExpression).Concat(SplitAnd(and.SecondExpression))
            : [expression];

    private static bool SameColumn(ShapeColumn a, ShapeColumn b) =>
        a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)
        && (a.Prefix.Length == 0 || b.Prefix.Length == 0 || a.Prefix.Equals(b.Prefix, StringComparison.OrdinalIgnoreCase));

    private List<string> FindExtraColumns(QuerySpecification spec, OrderByClause? orderBy)
    {
        // SELECT * already shows everything.
        if (spec.SelectElements.Any(e => e is not SelectScalarExpression))
            return [];

        var selected = spec.SelectElements.Cast<SelectScalarExpression>().ToList();
        var shown = selected.Select(e => e.Expression).OfType<ColumnReferenceExpression>().Where(c => c.MultiPartIdentifier is not null).Select(ColumnVisitor.Describe).ToList();
        var aliases = selected.Where(e => e.ColumnName is not null).Select(e => e.ColumnName.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extras = new List<string>();
        void Add(ColumnReferenceExpression column)
        {
            var described = ColumnVisitor.Describe(column);
            var text = Text(column);
            if (!shown.Any(s => SameColumn(s, described)) && !extras.Contains(text, StringComparer.OrdinalIgnoreCase))
                extras.Add(text);
        }

        if (spec.WhereClause?.SearchCondition is { } where)
        {
            foreach (var column in ColumnVisitor.References(where))
                Add(column);
        }

        // ORDER BY can name a select-list alias, which isn't a column of any table.
        if (orderBy is not null)
        {
            foreach (var column in ColumnVisitor.References(orderBy).Where(c => c.MultiPartIdentifier.Count > 1 || !aliases.Contains(c.MultiPartIdentifier.Identifiers[0].Value)))
                Add(column);
        }

        return extras;
    }

    /// <summary>What a row shows before it's grouped: its group keys, the values the aggregates read and the columns WHERE reads.</summary>
    private List<ShapeRowColumn> FindRowColumns(QuerySpecification spec, OrderByClause? orderBy, List<ScalarExpression> keys, FunctionVisitor functions)
    {
        var columns = new List<ShapeRowColumn>();
        var named = new List<ShapeColumn>();

        void Add(ScalarExpression expression, string? header = null)
        {
            if (expression is ColumnReferenceExpression { MultiPartIdentifier: not null } column)
            {
                var described = ColumnVisitor.Describe(column);
                if (named.Any(n => SameColumn(n, described)))
                    return;
                named.Add(described);
            }

            var text = Text(expression);
            if (!columns.Any(c => c.Expression.Equals(text, StringComparison.OrdinalIgnoreCase)))
                columns.Add(new ShapeRowColumn(header ?? text, text));
        }

        foreach (var key in keys)
            Add(key);
        KeyColumns = columns.Count;

        // Window functions keep every row, so the row's other columns belong next to them.
        if (IsWindowed)
        {
            foreach (var element in spec.SelectElements.OfType<SelectScalarExpression>().Where(e => !FunctionVisitor.HasWindow(e.Expression)))
                Add(element.Expression, element.ColumnName?.Value);
        }

        // COUNT(*) and COUNT(1) read no column, so there's nothing to show for them.
        foreach (var argument in functions.Arguments.Where(a => ColumnVisitor.References(a).Count > 0))
            Add(argument);

        if (spec.WhereClause?.SearchCondition is { } where)
        {
            foreach (var column in ColumnVisitor.References(where))
                Add(column);
        }

        if (IsWindowed && orderBy is not null)
        {
            var aliases = spec.SelectElements.OfType<SelectScalarExpression>().Where(e => e.ColumnName is not null).Select(e => e.ColumnName.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var column in ColumnVisitor.References(orderBy).Where(c => c.MultiPartIdentifier.Count > 1 || !aliases.Contains(c.MultiPartIdentifier.Identifiers[0].Value)))
                Add(column);
        }

        // SELECT COUNT(*) FROM Vendors reads no column at all, but the rows it counts should still show up.
        if (columns.Count == 0)
            columns.Add(new ShapeRowColumn("Row", $"{Quote(Sources[0].Reference)}.{RowNumberColumn}"));

        return columns;
    }

    private static List<int> FindCalculatedColumns(QuerySpecification spec)
    {
        // * could be any number of columns, so positions after it aren't known.
        if (spec.SelectElements.Any(e => e is not SelectScalarExpression))
            return [];
        return spec.SelectElements.Cast<SelectScalarExpression>()
            .Select((element, i) => (element, i))
            .Where(x => x.element.Expression is not ColumnReferenceExpression)
            .Select(x => x.i)
            .ToList();
    }

    /// <summary>Selects one table's columns with row numbers in <paramref name="ordering"/>, so a row keeps its number in every query.</summary>
    public string SourceSql(ShapeSource source, string ordering) =>
        $"SELECT {Numbered(source, ordering)} ORDER BY {RowNumberColumn}";

    /// <summary>
    /// Selects every row of the FROM clause before WHERE, in the query's ORDER BY order. Each row has the query's own columns, then
    /// <see cref="ExtraColumns"/>, then the row number it took from each table (NULL where an outer join found no match), then 1 or 0 for
    /// each of <see cref="Conditions"/>. NULL (unknown) counts as 0, as it does in WHERE.
    /// </summary>
    public string PipelineSql(IReadOnlyList<string> orderings)
    {
        var columns = new StringBuilder(_selectList);
        for (var i = 0; i < ExtraColumns.Count; i++)
            columns.Append($", {ExtraColumns[i]} AS __hv_x{i}");
        columns.Append(RowNumberColumns());
        columns.Append(ConditionColumns());
        return $"SELECT {columns} {NumberedFrom(orderings)} {_orderBy}";
    }

    /// <summary>
    /// For grouped queries: selects every row of the FROM clause before WHERE, with <see cref="RowColumns"/>, the row number it took from each
    /// table, 1 or 0 for each of <see cref="Conditions"/>, 1 or 0 for the whole WHERE, and its group's number.
    /// Group numbers count up in key order among the rows that pass WHERE, which is how <see cref="GroupResultSql"/> numbers them too.
    /// </summary>
    public string GroupRowsSql(IReadOnlyList<string> orderings)
    {
        var columns = new StringBuilder(string.Join(", ", RowColumns.Select((c, i) => $"{c.Expression} AS __hv_v{i}")));
        columns.Append(RowNumberColumns());
        columns.Append(ConditionColumns());
        columns.Append(Where is null ? ", 1 AS __hv_p" : $", CASE WHEN ({Where}) THEN 1 ELSE 0 END AS __hv_p");

        var order = KeyColumns > 0 ? string.Join(", ", Enumerable.Range(0, KeyColumns).Select(i => $"q.__hv_v{i}")) : "(SELECT NULL)";

        // Numbered in an outer query, as OVER can't contain a subquery from WHERE or a constant key.
        return $"SELECT q.*, DENSE_RANK() OVER (PARTITION BY q.__hv_p ORDER BY {order}) AS __hv_grp " +
               $"FROM (SELECT {columns} {NumberedFrom(orderings)}) AS q";
    }

    /// <summary>
    /// For GROUP BY: runs the query without HAVING, DISTINCT and TOP, in its ORDER BY order. Each row has the query's own columns, then its
    /// group's number, then 0 for a group or more for a ROLLUP or CUBE summary row, then 1 or 0 for each of <see cref="HavingConditions"/>.
    /// </summary>
    public string GroupResultSql(IReadOnlyList<string> orderings)
    {
        var keys = string.Join(", ", GroupKeys);
        var (number, level) = (GroupKeys.Count > 0, SummaryRows) switch
        {
            (false, _) => ("1", "0"),
            (true, null) => ($"DENSE_RANK() OVER (ORDER BY {keys})", "0"),
            _ => ($"DENSE_RANK() OVER (PARTITION BY GROUPING_ID({keys}) ORDER BY {keys})", $"GROUPING_ID({keys})"),
        };

        var columns = new StringBuilder(_selectList);
        columns.Append($", {number} AS __hv_grp, {level} AS __hv_level");
        for (var i = 0; i < HavingConditions.Count; i++)
            columns.Append($", CASE WHEN ({HavingConditions[i]}) THEN 1 ELSE 0 END AS __hv_h{i}");

        return $"SELECT {columns} {NumberedFrom(orderings)} {WhereClause()} {_groupBy} {_orderBy}";
    }

    /// <summary>For window functions: runs the query without DISTINCT and TOP, in its ORDER BY order, adding the row number each row took from each table.</summary>
    public string WindowResultSql(IReadOnlyList<string> orderings) =>
        $"SELECT {_selectList}{RowNumberColumns()} {NumberedFrom(orderings)} {WhereClause()} {_orderBy}";

    /// <summary>
    /// Tests the only join's ON condition on every pair of rows, the nested loop a join conceptually runs, so the
    /// failed comparisons can be shown too.
    /// </summary>
    public string ComparisonsSql(IReadOnlyList<string> orderings)
    {
        var (left, right) = (Sources[0], Sources[1]);
        return $"SELECT {Quote(left.Reference)}.{RowNumberColumn}, {Quote(right.Reference)}.{RowNumberColumn}, " +
               $"CASE WHEN ({Joins[0].On}) THEN 1 ELSE 0 END " +
               $"FROM {NumberedTable(left, orderings[0])} CROSS JOIN {NumberedTable(right, orderings[1])}";
    }

    private string WhereClause() => Where is null ? "" : $"WHERE {Where}";

    private string RowNumberColumns() =>
        string.Concat(Sources.Select((s, k) => $", {Quote(s.Reference)}.{RowNumberColumn} AS __hv_k{k}"));

    private string ConditionColumns() =>
        string.Concat(Conditions.Select((c, i) => $", CASE WHEN ({c}) THEN 1 ELSE 0 END AS __hv_c{i}"));

    /// <summary>The FROM clause with each table swapped for a copy that has row numbers. Aliases stay, so the query's conditions still work.</summary>
    private string NumberedFrom(IReadOnlyList<string> orderings)
    {
        var from = new StringBuilder(_from);
        foreach (var (source, ordering) in Sources.Zip(orderings).OrderByDescending(s => s.First.Offset))
        {
            from.Remove(source.Offset - _fromOffset, source.Length);
            from.Insert(source.Offset - _fromOffset, NumberedTable(source, ordering));
        }

        return from.ToString();
    }

    private static string NumberedTable(ShapeSource source, string ordering) =>
        $"(SELECT {Numbered(source, ordering)}) AS {Quote(source.Reference)}";

    private static string Numbered(ShapeSource source, string ordering) =>
        $"{Quote(source.Reference)}.*, ROW_NUMBER() OVER (ORDER BY {ordering}) AS {RowNumberColumn} FROM {source.Table} AS {Quote(source.Reference)}";

    public static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private string Text(TSqlFragment fragment) => Query.Substring(fragment.StartOffset, fragment.FragmentLength);

    /// <summary>Finds aggregate and window functions in the query itself, not in its subqueries, which can use them freely.</summary>
    private sealed class FunctionVisitor : TSqlFragmentVisitor
    {
        public bool HasAggregate { get; private set; }
        public List<OverClause> Windows { get; } = [];

        /// <summary>What the query's aggregate and window functions read.</summary>
        public List<ScalarExpression> Arguments { get; } = [];

        public static bool HasWindow(TSqlFragment fragment)
        {
            var visitor = new FunctionVisitor();
            fragment.Accept(visitor);
            return visitor.Windows.Count > 0;
        }

        public override void Visit(FunctionCall node)
        {
            if (node.OverClause is not null)
                Windows.Add(node.OverClause);
            else if (Aggregates.Contains(node.FunctionName.Value))
                HasAggregate = true;
            else
                return;
            Arguments.AddRange(node.Parameters);
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
        }
    }

    /// <summary>Finds the columns an expression reads, leaving out subqueries, whose columns belong to their own tables.</summary>
    internal sealed class ColumnVisitor : TSqlFragmentVisitor
    {
        private readonly List<ColumnReferenceExpression> _columns = [];

        public static IReadOnlyList<ColumnReferenceExpression> References(TSqlFragment fragment)
        {
            var visitor = new ColumnVisitor();
            fragment.Accept(visitor);
            return visitor._columns;
        }

        public static IReadOnlyList<ShapeColumn> Find(TSqlFragment fragment) => References(fragment).Select(Describe).ToList();

        public static ShapeColumn Describe(ColumnReferenceExpression column)
        {
            var parts = column.MultiPartIdentifier.Identifiers;
            return new ShapeColumn(parts.Count > 1 ? parts[^2].Value : "", parts[^1].Value);
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier is not null)
                _columns.Add(node);
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
        }
    }
}
