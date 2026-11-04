using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.Core.Tests;

public class QueryShapeTests
{
    private static QueryShape Shape(string sql)
    {
        var shape = QueryShape.Analyze(sql);
        Assert.Empty(shape.Problems);
        return shape;
    }

    [Theory]
    [InlineData("SELECT VendorState, COUNT(*) FROM Vendors GROUP BY GROUPING SETS ((VendorState), ())", "GROUPING SETS")]
    [InlineData("SELECT VendorState, SUM(COUNT(*)) OVER () FROM Vendors GROUP BY VendorState", "window functions in a query with GROUP BY")]
    [InlineData("SELECT VendorName FROM Vendors UNION SELECT VendorState FROM Vendors", "UNION")]
    [InlineData("WITH v AS (SELECT * FROM Vendors) SELECT * FROM v", "common table expressions")]
    [InlineData("SELECT * FROM (SELECT * FROM Vendors) v", "a subquery in the FROM clause")]
    [InlineData("SELECT * FROM Terms t JOIN (SELECT * FROM Vendors) v ON t.TermsID = v.DefaultTermsID", "a subquery in the FROM clause")]
    [InlineData("SELECT * FROM Vendors v CROSS APPLY (SELECT 1 AS One) x", "CROSS APPLY")]
    [InlineData("SELECT * FROM Vendors ORDER BY VendorID OFFSET 5 ROWS", "OFFSET")]
    [InlineData("SELECT 1 + 1", "a query without a FROM clause")]
    [InlineData("SELECT * FROM Terms t JOIN Vendors v ON t.TermsID = v.DefaultTermsID ORDER BY 5", "a column number")]
    public void Says_which_feature_it_cant_show(string sql, string feature)
    {
        var problem = Assert.Single(QueryShape.Analyze(sql).Problems);

        Assert.Contains(feature, problem);
    }

    [Fact]
    public void A_query_with_a_mistake_reports_the_mistake()
    {
        var problem = Assert.Single(QueryShape.Analyze("SELEC * FROM Vendors").Problems);

        Assert.Contains("Syntax error", problem);
    }

    [Fact]
    public void Aggregates_inside_a_subquery_are_fine()
    {
        var shape = Shape("SELECT InvoiceTotal, (SELECT MAX(InvoiceTotal) FROM Invoices) AS Highest FROM Invoices WHERE InvoiceTotal > (SELECT AVG(InvoiceTotal) FROM Invoices)");

        Assert.Equal(["InvoiceTotal > (SELECT AVG(InvoiceTotal) FROM Invoices)"], shape.Conditions);
    }

    [Fact]
    public void Splits_WHERE_at_its_top_level_ANDs_only()
    {
        var shape = Shape("""
            SELECT * FROM Invoices
            WHERE InvoiceTotal BETWEEN 10 AND 20 AND (VendorID = 1 OR VendorID = 2 AND TermsID = 3) AND InvoiceNumber LIKE 'A AND B'
            """);

        Assert.Equal(
            ["InvoiceTotal BETWEEN 10 AND 20", "(VendorID = 1 OR VendorID = 2 AND TermsID = 3)", "InvoiceNumber LIKE 'A AND B'"],
            shape.Conditions);
    }

    [Fact]
    public void An_OR_stays_one_condition()
    {
        var shape = Shape("SELECT * FROM Invoices WHERE VendorID = 1 OR TermsID = 2");

        Assert.Equal(["VendorID = 1 OR TermsID = 2"], shape.Conditions);
    }

    [Fact]
    public void Finds_every_table_and_join_in_order()
    {
        var shape = Shape("""
            SELECT *
            FROM Vendors v
                LEFT OUTER JOIN Invoices AS i ON v.VendorID = i.VendorID
                CROSS JOIN Terms
                RIGHT JOIN dbo.GLAccounts g ON g.AccountNo = v.DefaultAccountNo
            """);

        Assert.Equal(["Vendors", "Invoices", "Terms", "dbo.GLAccounts"], shape.Sources.Select(s => s.Table));
        Assert.Equal(["v", "i", "Terms", "g"], shape.Sources.Select(s => s.Reference));
        Assert.Equal(["LEFT JOIN", "CROSS JOIN", "RIGHT JOIN"], shape.Joins.Select(j => j.Type));
        Assert.Equal("v.VendorID = i.VendorID", shape.Joins[0].On);
        Assert.Null(shape.Joins[1].On);
    }

    [Fact]
    public void Commas_between_tables_are_joins_too()
    {
        var shape = Shape("SELECT * FROM Vendors v, Invoices i JOIN Terms t ON i.TermsID = t.TermsID WHERE v.VendorID = i.VendorID");

        Assert.Equal(["v", "i", "t"], shape.Sources.Select(s => s.Reference));
        Assert.Equal([",", "JOIN"], shape.Joins.Select(j => j.Type));
    }

    [Fact]
    public void Adds_the_columns_WHERE_and_ORDER_BY_read_that_the_select_list_leaves_out()
    {
        var shape = Shape("""
            SELECT v.VendorName, i.InvoiceTotal AS Total
            FROM Vendors v JOIN Invoices i ON v.VendorID = i.VendorID
            WHERE i.PaymentDate IS NULL AND i.InvoiceTotal > 100 AND EXISTS (SELECT 1 FROM Terms t WHERE t.TermsID = i.TermsID)
            ORDER BY Total, v.VendorState
            """);

        // Total is an alias, InvoiceTotal is already selected, and the subquery's columns belong to the subquery.
        Assert.Equal(["i.PaymentDate", "v.VendorState"], shape.ExtraColumns);
    }

    [Fact]
    public void SELECT_star_needs_no_extra_columns()
    {
        var shape = Shape("SELECT * FROM Vendors WHERE VendorState = 'CA' ORDER BY VendorCity");

        Assert.Empty(shape.ExtraColumns);
    }

    [Fact]
    public void Numbers_each_table_and_keeps_its_alias()
    {
        var shape = Shape("""
            SELECT TOP 5 v.VendorName
            FROM Vendors v JOIN Invoices ON v.VendorID = Invoices.VendorID
            WHERE v.VendorState = 'CA'
            ORDER BY v.VendorName DESC
            """);

        var sql = shape.PipelineSql(["[v].[VendorID]", "[Invoices].[InvoiceID]"]);

        Assert.Contains("FROM (SELECT [v].*, ROW_NUMBER() OVER (ORDER BY [v].[VendorID]) AS __hv_rn FROM Vendors AS [v]) AS [v] JOIN", sql);
        Assert.Contains("AS __hv_rn FROM Invoices AS [Invoices]) AS [Invoices] ON v.VendorID = Invoices.VendorID", sql);
        Assert.Contains("CASE WHEN (v.VendorState = 'CA') THEN 1 ELSE 0 END AS __hv_c0", sql);
        Assert.EndsWith("ORDER BY v.VendorName DESC", sql);
        Assert.DoesNotContain("TOP", sql);
        Assert.DoesNotContain("WHERE", sql);
    }

    [Fact]
    public void Finds_group_keys_and_what_the_aggregates_read()
    {
        var shape = Shape("""
            SELECT v.VendorState, COUNT(*) AS Invoices, SUM(i.InvoiceTotal - i.CreditTotal) AS Owed, COUNT(DISTINCT i.TermsID) AS Terms
            FROM Vendors v JOIN Invoices i ON v.VendorID = i.VendorID
            WHERE i.PaymentDate IS NULL
            GROUP BY v.VendorState
            HAVING COUNT(*) > 1 AND MAX(i.InvoiceDate) > '2023-01-01'
            ORDER BY Owed DESC
            """);

        Assert.True(shape.IsGrouped);
        Assert.Equal(["v.VendorState"], shape.GroupKeys);
        Assert.Equal("GROUP BY v.VendorState", shape.GroupClause);
        Assert.Equal(["COUNT(*) > 1", "MAX(i.InvoiceDate) > '2023-01-01'"], shape.HavingConditions);

        // COUNT(*) reads no column. WHERE's columns are shown too, so students can see why a row was left out of its group.
        Assert.Equal(
            ["v.VendorState", "i.InvoiceTotal - i.CreditTotal", "i.TermsID", "i.InvoiceDate", "i.PaymentDate"],
            shape.RowColumns.Select(c => c.Expression));
        Assert.Equal(1, shape.KeyColumns);
        Assert.Equal([1, 2, 3], shape.CalculatedColumns);
    }

    [Fact]
    public void An_aggregate_without_GROUP_BY_is_one_group()
    {
        var shape = Shape("SELECT COUNT(*) AS OrderCount, SUM(TaxAmount) AS TaxTotal FROM Orders");

        Assert.True(shape.IsGrouped);
        Assert.Empty(shape.GroupKeys);
        Assert.Null(shape.GroupClause);
    }

    [Theory]
    [InlineData("SELECT AccountNo, SUM(InvoiceLineItemAmount) FROM InvoiceLineItems GROUP BY AccountNo WITH ROLLUP", "WITH ROLLUP")]
    [InlineData("SELECT AccountNo, SUM(InvoiceLineItemAmount) FROM InvoiceLineItems GROUP BY AccountNo WITH CUBE", "WITH CUBE")]
    [InlineData("SELECT AccountNo, SUM(InvoiceLineItemAmount) FROM InvoiceLineItems GROUP BY ROLLUP (AccountNo)", "ROLLUP")]
    [InlineData("SELECT AccountNo, SUM(InvoiceLineItemAmount) FROM InvoiceLineItems GROUP BY CUBE (AccountNo)", "CUBE")]
    public void Recognizes_the_ways_to_ask_for_summary_rows(string sql, string summaryRows)
    {
        var shape = Shape(sql);

        Assert.Equal(summaryRows, shape.SummaryRows);
        Assert.Equal(["AccountNo"], shape.GroupKeys);
    }

    [Fact]
    public void A_window_function_partitions_without_grouping()
    {
        var shape = Shape("""
            SELECT VendorID, InvoiceTotal, SUM(InvoiceTotal) OVER (PARTITION BY VendorID) AS VendorTotal
            FROM Invoices
            WHERE InvoiceTotal > 100
            """);

        Assert.True(shape.IsWindowed);
        Assert.False(shape.IsGrouped);
        Assert.Equal("PARTITION BY VendorID", shape.GroupClause);
        Assert.Equal(["VendorID", "InvoiceTotal"], shape.RowColumns.Select(c => c.Expression));
        Assert.False(shape.MixedPartitions);
    }

    [Fact]
    public void Notices_window_functions_that_partition_differently()
    {
        var shape = Shape("SELECT SUM(InvoiceTotal) OVER (PARTITION BY VendorID), SUM(InvoiceTotal) OVER (PARTITION BY TermsID) FROM Invoices");

        Assert.True(shape.MixedPartitions);
    }

    [Fact]
    public void Remembers_DISTINCT_and_TOP()
    {
        var shape = Shape("SELECT DISTINCT TOP 10 PERCENT VendorCity FROM Vendors ORDER BY VendorCity");

        Assert.True(shape.Distinct);
        Assert.Equal("TOP 10 PERCENT", shape.Top);
        Assert.Equal(["VendorCity"], shape.SortKeys);
    }
}
