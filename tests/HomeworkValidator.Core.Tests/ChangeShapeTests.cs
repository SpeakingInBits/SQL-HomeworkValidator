using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.Core.Tests;

public class ChangeShapeTests
{
    private static ChangeShape Shape(string sql)
    {
        var shape = ChangeShape.Analyze(sql);
        Assert.Empty(shape.Problems);
        return shape;
    }

    [Fact]
    public void Reads_an_UPDATE()
    {
        var change = Assert.Single(Shape("UPDATE VendorCopy\nSET DefaultAccountNo = 403, DefaultTermsID = 2\nWHERE DefaultAccountNo = 400 AND VendorState = 'CA';").Changes);

        Assert.Equal(ChangeKind.Update, change.Kind);
        Assert.Equal("VendorCopy", change.Table);
        Assert.Equal("DefaultAccountNo = 403, DefaultTermsID = 2", change.Set);
        Assert.Equal(["DefaultAccountNo = 400", "VendorState = 'CA'"], change.Conditions);
        Assert.Equal(["DefaultAccountNo", "DefaultTermsID", "VendorState"], change.ReadColumns);
    }

    [Fact]
    public void Reads_a_DELETE_with_and_without_FROM()
    {
        Assert.Equal("VendorCopy", Assert.Single(Shape("DELETE VendorCopy WHERE VendorState = 'MN';").Changes).Table);
        Assert.Equal("VendorCopy", Assert.Single(Shape("DELETE FROM VendorCopy WHERE VendorState = 'MN';").Changes).Table);
    }

    [Fact]
    public void Reads_an_INSERTs_column_list()
    {
        var change = Assert.Single(Shape("INSERT INTO Categories (CategoryName) VALUES ('Brass');").Changes);

        Assert.Equal(ChangeKind.Insert, change.Kind);
        Assert.Equal(["CategoryName"], change.InsertColumns);
    }

    [Fact]
    public void A_statement_with_a_FROM_clause_cant_test_conditions_on_the_table_alone()
    {
        var change = Assert.Single(Shape("""
            UPDATE i
            SET TermsID = 2
            FROM InvoiceCopy i
                JOIN VendorCopy v ON i.VendorID = v.VendorID
            WHERE DefaultTermsID = 2;
            """).Changes);

        Assert.Equal("InvoiceCopy", change.Table);
        Assert.Equal("DefaultTermsID = 2", change.Where);
        Assert.Empty(change.Conditions);
    }

    [Fact]
    public void Adds_OUTPUT_in_the_right_place_for_each_statement()
    {
        var update = Shape("UPDATE Terms SET TermsDueDays = 5 WHERE TermsID = 1;").Batches[0];
        var delete = Shape("DELETE Terms WHERE TermsID = 1;").Batches[0];
        var join = Shape("DELETE t FROM Terms t JOIN Vendors v ON t.TermsID = v.DefaultTermsID;").Batches[0];
        var insert = Shape("INSERT INTO Terms (TermsDescription, TermsDueDays) VALUES ('x', 1);").Batches[0];
        var select = Shape("INSERT INTO Terms (TermsDescription, TermsDueDays) SELECT 'x', 1;").Batches[0];

        Assert.Contains("SET TermsDueDays = 5 OUTPUT 0 AS [__hv_out_0], deleted.*, inserted.*  WHERE", update);
        Assert.Contains("DELETE Terms OUTPUT 0 AS [__hv_out_0], deleted.*  WHERE", delete);
        Assert.Contains("DELETE t OUTPUT 0 AS [__hv_out_0], deleted.*  FROM Terms t", join);
        Assert.Contains("(TermsDescription, TermsDueDays) OUTPUT 0 AS [__hv_out_0], inserted.* VALUES", insert.Replace("  ", " "));
        Assert.Contains("(TermsDescription, TermsDueDays) OUTPUT 0 AS [__hv_out_0], inserted.* SELECT", select.Replace("  ", " "));
    }

    [Fact]
    public void Reads_the_table_just_before_each_statement_and_tests_its_conditions()
    {
        var batch = Shape("UPDATE Terms SET TermsDueDays = 5 WHERE TermsID = 1;").Batches[0];

        Assert.StartsWith("SELECT 0 AS [__hv_before_0], Terms.*, CASE WHEN (TermsID = 1) THEN 1 ELSE 0 END FROM Terms; UPDATE Terms", batch);
    }

    [Fact]
    public void Line_numbers_stay_where_they_were()
    {
        const string sql = "DECLARE @Days int = 5;\n\nUPDATE Terms\nSET TermsDueDays = @Days\nWHERE TermsID = 1;";

        Assert.Equal(sql.Count(c => c == '\n'), Shape(sql).Batches[0].Count(c => c == '\n'));
    }

    [Fact]
    public void Numbers_statements_across_batches()
    {
        var shape = Shape("DELETE Products WHERE CategoryID = 4;\nGO\nDELETE Categories WHERE CategoryID = 4;");

        Assert.Equal(2, shape.Changes.Count);
        Assert.Equal(2, shape.Batches.Count);
        Assert.Contains("__hv_out_0", shape.Batches[0]);
        Assert.Contains("__hv_out_1", shape.Batches[1]);
    }

    [Fact]
    public void Statements_inside_a_block_run_but_arent_shown()
    {
        var shape = Shape("IF 1 = 1\n    DELETE Terms WHERE TermsID = 1;");

        Assert.Empty(shape.Changes);
        Assert.Contains("inside IF", Assert.Single(shape.Notes));
    }

    [Fact]
    public void Statements_with_their_own_OUTPUT_run_but_arent_shown()
    {
        var shape = Shape("DELETE Terms OUTPUT deleted.TermsID WHERE TermsID = 1;");

        Assert.Empty(shape.Changes);
        Assert.Single(shape.Notes);
    }

    [Fact]
    public void Other_statements_are_left_alone()
    {
        var shape = Shape("SELECT * INTO TermsCopy FROM Terms;");

        Assert.Empty(shape.Changes);
        Assert.Empty(shape.Notes);
        Assert.Equal("SELECT * INTO TermsCopy FROM Terms;", shape.Batches[0].Trim());
    }

    [Fact]
    public void Refuses_what_a_check_refuses()
    {
        Assert.NotEmpty(ChangeShape.Analyze("EXEC xp_cmdshell 'dir';").Problems);
        Assert.NotEmpty(ChangeShape.Analyze("UPDATE master.dbo.Terms SET TermsDueDays = 1;").Problems);
        Assert.NotEmpty(ChangeShape.Analyze("UPDAT Terms;").Problems);
    }
}
