using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Tests;

public class DmlAnalyzerTests
{
    [Fact]
    public void Typical_homework_script_is_runnable()
    {
        const string sql = """
            IF OBJECT_ID('VendorCopy') IS NOT NULL
                DROP TABLE VendorCopy;
            DROP TABLE IF EXISTS InvoiceCopy;

            SELECT * INTO VendorCopy FROM Vendors;
            SELECT * INTO InvoiceCopy FROM Invoices;
            SELECT * FROM VendorCopy;
            """;

        var result = ScriptAnalyzer.AnalyzeDml(sql);

        Assert.True(result.IsRunnable, string.Join(" ", result.Errors));
        Assert.Contains(SqlFeature.SelectInto, result.Features);
    }

    [Fact]
    public void Each_go_batch_runs_separately_and_keeps_its_line_numbers()
    {
        var result = ScriptAnalyzer.AnalyzeDml("INSERT Categories (CategoryName) VALUES ('Brass');\nGO\nUPDATE Categories SET CategoryName = 'Woodwinds' WHERE CategoryID = 5;");

        Assert.Equal(2, result.Batches.Count);
        Assert.StartsWith("INSERT", result.Batches[0]);
        Assert.StartsWith("\n\nUPDATE", result.Batches[1]);
    }

    [Fact]
    public void Use_statements_are_blanked_out_without_moving_lines()
    {
        var result = ScriptAnalyzer.AnalyzeDml("USE MyGuitarShop;\nDELETE Categories\nWHERE CategoryID = 5;");

        Assert.True(result.IsRunnable);
        Assert.Single(result.Notes);
        Assert.DoesNotContain("USE", result.Batches[0]);
        Assert.Equal(3, result.Batches[0].Split('\n').Length);
    }

    [Fact]
    public void Transactions_and_variables_are_allowed()
    {
        const string sql = """
            BEGIN TRAN;
            DECLARE @id int = 5;
            UPDATE Categories SET CategoryName = 'Woodwinds' WHERE CategoryID = @id;
            COMMIT;
            """;

        Assert.True(ScriptAnalyzer.AnalyzeDml(sql).IsRunnable);
    }

    [Theory]
    [InlineData("EXEC sp_who;", "EXECUTE")]
    [InlineData("EXEC('DELETE Vendors');", "EXECUTE")]
    [InlineData("DROP DATABASE AP;", "DROP DATABASE")]
    [InlineData("CREATE LOGIN hacker WITH PASSWORD = 'x';", "CREATE LOGIN")]
    [InlineData("IF 1 = 1 EXEC sp_who;", "EXECUTE")]
    [InlineData("BEGIN DROP DATABASE AP; END", "DROP DATABASE")]
    public void Statements_that_reach_outside_the_database_are_refused(string sql, string expected)
    {
        var result = ScriptAnalyzer.AnalyzeDml(sql);

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Database_names_are_refused()
    {
        var result = ScriptAnalyzer.AnalyzeDml("DELETE AP.dbo.VendorCopy WHERE VendorState = 'MN';");

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Errors, e => e.Contains("remove the database name"));
    }

    [Theory]
    [InlineData("INSERT INTO Categories VALUES ('Brass');", true)]
    [InlineData("INSERT INTO Categories (CategoryName) VALUES ('Brass');", false)]
    public void Detects_inserts_without_a_column_list(string sql, bool expected)
    {
        Assert.Equal(expected, ScriptAnalyzer.AnalyzeDml(sql).Features.Contains(SqlFeature.InsertWithoutColumnList));
    }

    [Fact]
    public void Update_with_a_subquery_has_a_subquery_and_a_where()
    {
        var features = ScriptAnalyzer.AnalyzeDml(
            "UPDATE InvoiceCopy SET TermsID = 2 WHERE VendorID IN (SELECT VendorID FROM VendorCopy WHERE DefaultTermsID = 2);").Features;

        Assert.Contains(SqlFeature.Subquery, features);
        Assert.Contains(SqlFeature.Where, features);
        Assert.DoesNotContain(SqlFeature.Join, features);
    }

    [Theory]
    [InlineData("UPDATE InvoiceCopy SET TermsID = 2 FROM InvoiceCopy JOIN VendorCopy ON InvoiceCopy.VendorID = VendorCopy.VendorID WHERE DefaultTermsID = 2;")]
    [InlineData("UPDATE InvoiceCopy SET TermsID = 2 FROM InvoiceCopy, VendorCopy WHERE InvoiceCopy.VendorID = VendorCopy.VendorID AND DefaultTermsID = 2;")]
    [InlineData("DELETE VendorCopy FROM VendorCopy JOIN InvoiceCopy ON VendorCopy.VendorID = InvoiceCopy.VendorID;")]
    public void Update_and_delete_joins_are_detected(string sql)
    {
        var features = ScriptAnalyzer.AnalyzeDml(sql).Features;

        Assert.Contains(SqlFeature.Join, features);
        Assert.DoesNotContain(SqlFeature.Subquery, features);
    }

    [Fact]
    public void Analyze_picks_the_rules_for_the_question_kind()
    {
        const string sql = "DELETE Categories WHERE CategoryID = 5;";

        Assert.True(ScriptAnalyzer.Analyze(sql, QuestionKind.Dml).IsRunnable);
        Assert.False(ScriptAnalyzer.Analyze(sql, QuestionKind.Select).IsRunnable);
    }
}
