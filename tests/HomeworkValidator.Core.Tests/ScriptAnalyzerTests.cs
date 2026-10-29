using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Tests;

public class ScriptAnalyzerTests
{
    [Fact]
    public void Single_select_is_runnable()
    {
        var result = ScriptAnalyzer.AnalyzeSelect("SELECT VendorName FROM Vendors ORDER BY VendorName;");

        Assert.True(result.IsRunnable);
        Assert.Contains(SqlFeature.From, result.Features);
        Assert.Contains(SqlFeature.OrderBy, result.Features);
    }

    [Fact]
    public void Use_statement_is_dropped_and_line_numbers_are_kept()
    {
        var result = ScriptAnalyzer.AnalyzeSelect("USE AP;\nGO\n\nSELECT VendorName\nFROM Vendors;");

        Assert.True(result.IsRunnable);
        Assert.Single(result.Notes);
        Assert.DoesNotContain("USE", result.Batches[0]);
        // The SELECT started on line 4, so three blank lines come first.
        Assert.StartsWith("\n\n\nSELECT", result.Batches[0]);
    }

    [Fact]
    public void Two_selects_are_rejected()
    {
        var result = ScriptAnalyzer.AnalyzeSelect("SELECT 1; SELECT 2;");

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Errors, e => e.Contains("single query"));
    }

    [Theory]
    [InlineData("DELETE FROM Vendors;", "DELETE")]
    [InlineData("SELECT * INTO Copy FROM Vendors;", "SELECT INTO")]
    [InlineData("DROP TABLE Vendors;", "DROP TABLE")]
    public void Statements_other_than_select_are_rejected(string sql, string expected)
    {
        var result = ScriptAnalyzer.AnalyzeSelect(sql);

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Errors, e => e.Contains(expected));
    }

    [Theory]
    [InlineData("SELECT * FROM AP.dbo.Vendors;")]
    [InlineData("SELECT * FROM Vendors v JOIN OtherDb.dbo.Invoices i ON v.VendorID = i.VendorID;")]
    public void Database_names_are_rejected(string sql)
    {
        var result = ScriptAnalyzer.AnalyzeSelect(sql);

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Errors, e => e.Contains("remove the database name"));
    }

    [Fact]
    public void Syntax_errors_report_line_and_column()
    {
        var result = ScriptAnalyzer.AnalyzeSelect("SELECT VendorName\nFROM Vendors\nORDER BY VendorName,;");

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Errors, e => e.StartsWith("Syntax error on line 3"));
    }

    [Theory]
    [InlineData("SELECT InvoiceTotal AS Total FROM Invoices;", false)]
    [InlineData("SELECT InvoiceTotal AS [Total Amount] FROM Invoices;", false)]
    [InlineData("SELECT InvoiceTotal AS 'Total' FROM Invoices;", false)]
    [InlineData("SELECT InvoiceTotal FROM Invoices;", false)]
    [InlineData("SELECT InvoiceTotal Total FROM Invoices;", true)]
    [InlineData("SELECT Total = InvoiceTotal FROM Invoices;", true)]
    [InlineData("SELECT InvoiceTotal AS Total, PaymentTotal Paid FROM Invoices;", true)]
    public void Detects_aliases_without_as(string sql, bool expected)
    {
        var result = ScriptAnalyzer.AnalyzeSelect(sql);

        Assert.Equal(expected, result.Features.Contains(SqlFeature.AliasWithoutAs));
    }

    [Theory]
    [InlineData("SELECT 1 AS One;", new SqlFeature[0])]
    [InlineData("SELECT * FROM Vendors v JOIN Invoices i ON v.VendorID = i.VendorID;", new[] { SqlFeature.From, SqlFeature.Join, SqlFeature.SelectStar })]
    [InlineData("SELECT VendorID FROM Vendors, Invoices;", new[] { SqlFeature.From, SqlFeature.Join })]
    [InlineData("SELECT VendorID FROM Vendors WHERE VendorID IN (SELECT VendorID FROM Invoices);", new[] { SqlFeature.From, SqlFeature.Where, SqlFeature.Subquery })]
    [InlineData("SELECT DISTINCT TOP 5 VendorState FROM Vendors GROUP BY VendorState HAVING COUNT(*) > 1;", new[] { SqlFeature.From, SqlFeature.Distinct, SqlFeature.Top, SqlFeature.GroupBy, SqlFeature.Having })]
    [InlineData("WITH v AS (SELECT VendorID FROM Vendors) SELECT VendorID FROM v;", new[] { SqlFeature.Cte, SqlFeature.From })]
    [InlineData("SELECT VendorID FROM Vendors UNION SELECT VendorID FROM Invoices;", new[] { SqlFeature.From, SqlFeature.Union })]
    public void Detects_features(string sql, SqlFeature[] expected)
    {
        var result = ScriptAnalyzer.AnalyzeSelect(sql);

        Assert.True(result.IsRunnable, string.Join(" ", result.Errors));
        Assert.Equal(expected.Order(), result.Features.Order());
    }

    [Fact]
    public void Window_function_order_by_is_not_an_order_by_clause()
    {
        var result = ScriptAnalyzer.AnalyzeSelect("SELECT ROW_NUMBER() OVER (ORDER BY VendorName) AS RowNum FROM Vendors;");

        Assert.DoesNotContain(SqlFeature.OrderBy, result.Features);
    }

    [Fact]
    public void Rules_report_missing_and_forbidden_features()
    {
        var rules = new QuestionRules(new HashSet<SqlFeature> { SqlFeature.Where }, new HashSet<SqlFeature> { SqlFeature.From });
        var features = new HashSet<SqlFeature> { SqlFeature.From };

        var violations = ScriptAnalyzer.CheckRules(features, rules);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains("requires a WHERE clause"));
        Assert.Contains(violations, v => v.Contains("doesn't allow a FROM clause"));
    }
}
