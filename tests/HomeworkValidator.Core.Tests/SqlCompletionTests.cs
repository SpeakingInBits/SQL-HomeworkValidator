using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Tests;

public class SqlCompletionTests
{
    private static readonly SqlCatalog Catalog = SqlCatalog.FromScript("""
        CREATE TABLE Vendors (
            VendorID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
            VendorName varchar(50) NOT NULL,
            VendorState char(2) NOT NULL
        );
        GO
        CREATE TABLE dbo.Invoices (
            InvoiceID int NOT NULL,
            VendorID int NOT NULL,
            InvoiceTotal money NOT NULL
        );
        GO
        ALTER TABLE Invoices ADD PaymentDate date NULL;
        """);

    /// <summary>Completes at the | in the SQL.</summary>
    private static CompletionResult Complete(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        return SqlCompletion.Complete(sqlWithCaret.Remove(caret, 1), caret, Catalog);
    }

    private static List<string> Texts(CompletionResult result, CompletionKind kind) =>
        result.Items.Where(i => i.Kind == kind).Select(i => i.Text).ToList();

    [Fact]
    public void Catalog_reads_tables_and_columns_from_the_schema_script()
    {
        Assert.Equal(["Invoices", "Vendors"], Catalog.Tables.Select(t => t.Name).Order());
        Assert.Equal(["InvoiceID", "VendorID", "InvoiceTotal", "PaymentDate"], Catalog.Find("invoices")!.Columns.Select(c => c.Name));
        Assert.Equal("varchar(50)", Catalog.Find("Vendors")!.Columns[1].Type);
        Assert.Equal("money", Catalog.Find("Invoices")!.Columns[2].Type);
    }

    [Fact]
    public void Catalog_reads_the_course_databases()
    {
        var content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));
        var ap = SqlCatalog.FromScript(content.GetDatabase("AP").SchemaSql);

        Assert.Contains("VendorName", ap.Find("Vendors")!.Columns.Select(c => c.Name));
        Assert.Contains("InvoiceTotal", ap.Find("Invoices")!.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Select_list_shows_the_columns_of_the_tables_in_from_without_typing_the_table_name()
    {
        var result = Complete("SELECT Ven| FROM Vendors");

        Assert.Equal(7, result.Start);
        Assert.Equal(["VendorID", "VendorName", "VendorState"], Texts(result, CompletionKind.Column));
        Assert.Contains("COUNT", Texts(result, CompletionKind.Function));
        Assert.Contains("FROM", Texts(result, CompletionKind.Keyword));
    }

    [Fact]
    public void Columns_come_from_every_joined_table_once_each()
    {
        var result = Complete("SELECT | FROM Vendors v JOIN Invoices i ON v.VendorID = i.VendorID");

        var columns = Texts(result, CompletionKind.Column);
        Assert.Equal(columns.Distinct().Count(), columns.Count);
        Assert.Contains("InvoiceTotal", columns);
        Assert.Contains("VendorName", columns);
        Assert.Equal("in Vendors, Invoices", result.Items.Single(i => i.Text == "VendorID").Detail);
        Assert.Equal("money · Invoices", result.Items.Single(i => i.Text == "InvoiceTotal").Detail);
        Assert.Equal(["i", "v"], Texts(result, CompletionKind.Table));
    }

    [Theory]
    [InlineData("SELECT * FROM Vendors WHERE |")]
    [InlineData("SELECT * FROM Vendors v JOIN Invoices i ON |")]
    [InlineData("SELECT VendorState, COUNT(*) FROM Vendors GROUP BY |")]
    [InlineData("SELECT * FROM Vendors ORDER BY |")]
    [InlineData("SELECT VendorState FROM Vendors GROUP BY VendorState HAVING COUNT(|) > 1")]
    [InlineData("UPDATE Vendors SET |")]
    [InlineData("DELETE FROM Vendors WHERE |")]
    [InlineData("SELECT * FROM Vendors WHERE VendorID IN (SELECT VendorID FROM Invoices WHERE |)")]
    [InlineData("SELECT * FROM Vendors WHERE VendorName LIKE 'A%' AND |")]
    public void Expression_clauses_show_columns(string sql)
    {
        Assert.Contains("VendorState", Texts(Complete(sql), CompletionKind.Column));
    }

    [Fact]
    public void Select_list_without_a_from_clause_shows_no_columns()
    {
        var result = Complete("SELECT |");

        Assert.Empty(Texts(result, CompletionKind.Column));
        Assert.Contains("SELECT", Texts(result, CompletionKind.Keyword));
    }

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("SELECT * FROM Vendors v JOIN |")]
    [InlineData("SELECT * FROM Vendors v INNER JOIN Invoices i ON v.VendorID = i.VendorID LEFT JOIN |")]
    [InlineData("SELECT * FROM Vendors, |")]
    [InlineData("INSERT INTO |")]
    [InlineData("UPDATE |")]
    [InlineData("DELETE |")]
    public void Table_positions_show_tables(string sql)
    {
        var result = Complete(sql);

        Assert.Equal(["Invoices", "Vendors"], Texts(result, CompletionKind.Table));
        Assert.Empty(Texts(result, CompletionKind.Column));
    }

    [Theory]
    [InlineData("SELECT v.| FROM Vendors v")]
    [InlineData("SELECT Vendors.| FROM Vendors")]
    [InlineData("SELECT * FROM Vendors AS v WHERE v.Ven|")]
    public void Alias_dot_shows_that_tables_columns_only(string sql)
    {
        var result = Complete(sql);

        Assert.Equal(["VendorID", "VendorName", "VendorState"], Texts(result, CompletionKind.Column));
        Assert.Empty(Texts(result, CompletionKind.Keyword));
    }

    [Fact]
    public void Dbo_dot_shows_tables()
    {
        Assert.Equal(["Invoices", "Vendors"], Texts(Complete("SELECT * FROM dbo.|"), CompletionKind.Table));
    }

    [Fact]
    public void Insert_column_list_shows_that_tables_columns()
    {
        var result = Complete("INSERT INTO Invoices (InvoiceID, |");

        Assert.Equal(["InvoiceID", "VendorID", "InvoiceTotal", "PaymentDate"], Texts(result, CompletionKind.Column));
    }

    [Fact]
    public void Each_statement_has_its_own_tables_even_without_semicolons()
    {
        var result = Complete("""
            SELECT * FROM Invoices
            SELECT | FROM Vendors
            """);

        Assert.Contains("VendorName", Texts(result, CompletionKind.Column));
        Assert.DoesNotContain("InvoiceTotal", Texts(result, CompletionKind.Column));
    }

    [Fact]
    public void Union_keeps_both_queries_in_one_statement()
    {
        var result = Complete("SELECT | FROM Vendors UNION SELECT InvoiceID FROM Invoices");

        Assert.Contains("InvoiceTotal", Texts(result, CompletionKind.Column));
    }

    [Fact]
    public void Insert_select_reads_from_the_select()
    {
        var result = Complete("INSERT INTO Invoices (VendorID) SELECT | FROM Vendors");

        Assert.Contains("VendorName", Texts(result, CompletionKind.Column));
    }

    [Theory]
    [InlineData("SELECT 'Ven| FROM Vendors")]
    [InlineData("SELECT * FROM Vendors -- Ven|")]
    [InlineData("SELECT * /* Ven| */ FROM Vendors")]
    [InlineData("SELECT VendorName AS |")]
    [InlineData("SELECT * FROM Vendors AS |")]
    [InlineData("SELECT 12| FROM Vendors")]
    [InlineData("DECLARE @Ve|")]
    public void Nothing_is_suggested_in_strings_comments_aliases_numbers_or_variables(string sql)
    {
        Assert.Empty(Complete(sql).Items);
    }

    [Fact]
    public void After_a_table_name_shows_keywords_not_tables()
    {
        var result = Complete("SELECT * FROM Vendors W|");

        Assert.Empty(Texts(result, CompletionKind.Table));
        Assert.Contains("WHERE", Texts(result, CompletionKind.Keyword));
    }

    [Fact]
    public void Start_of_a_statement_shows_keywords()
    {
        var result = Complete("SELECT * FROM Vendors;\nS|");

        Assert.Equal(23, result.Start);
        Assert.Contains("SELECT", Texts(result, CompletionKind.Keyword));
        Assert.Empty(Texts(result, CompletionKind.Column));
    }

    [Fact]
    public void Names_that_need_brackets_get_them()
    {
        var catalog = SqlCatalog.FromScript("CREATE TABLE [Order Lines] ([Line Total] money, [Order] int)");

        var result = SqlCompletion.Complete("SELECT  FROM [Order Lines]", 7, catalog);

        Assert.Contains("[Line Total]", Texts(result, CompletionKind.Column));
        Assert.Contains("[Order]", Texts(result, CompletionKind.Column));
    }
}
