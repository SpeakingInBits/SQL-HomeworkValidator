using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using HomeworkValidator.Core.Sql;
using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Traces INSERT, UPDATE and DELETE statements on a real SQL Server, and checks that what the animation ends on is what the
/// script really does, and that nothing stays changed. Needs HV_TEST_CONNECTION_STRING and the validator's databases.
/// </summary>
[Trait("Category", "NeedsSqlServer")]
[Collection(SqlServerCollection.Name)]
public class VisualizerChangeServerTests
{
    private const string AP = "HV_AP_base";
    private const string Guitars = "HV_MyGuitarShop_base";

    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    private static ServerConnection Server()
    {
        var connectionString = Environment.GetEnvironmentVariable("HV_TEST_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "HV_TEST_CONNECTION_STRING isn't set.");
        return new ServerConnection(connectionString!);
    }

    private static async Task<ChangeOutcome> TraceAsync(string database, string sql, params string[] setup) =>
        await new ChangeTracer(Server()).TraceAsync(database, setup, sql, TestContext.Current.CancellationToken);

    private static async Task<TraceChange> OnlyChangeAsync(string database, string sql, params string[] setup)
    {
        var outcome = await TraceAsync(database, sql, setup);
        Assert.Empty(outcome.Problems);
        return Assert.Single(outcome.Changes);
    }

    private static async Task<int> CountAsync(string database, string table)
    {
        var result = await new QueryRunner(Server()).RunAsync(database, $"SELECT COUNT(*) FROM {table}", restricted: true, TestContext.Current.CancellationToken);
        return (int)result.Rows[0][0]!;
    }

    [Fact]
    public async Task Every_solution_ends_on_the_tables_real_contents()
    {
        var server = Server();
        var databases = new DatabaseManager(server);
        var checker = new Checker(Content, databases, server);
        var tracer = new ChangeTracer(server);
        var runner = new ScriptRunner(server);
        var ct = TestContext.Current.CancellationToken;
        var compared = 0;
        var problems = new List<string>();

        foreach (var question in Content.Assignments.SelectMany(a => a.Questions).Where(q => q.Kind == QuestionKind.Dml))
        {
            var database = Content.GetDatabase(question.Database);
            var name = DatabaseManager.DatabaseName(database, database.GetDataset(question.Datasets[0]));
            var setup = checker.SetupBatches(question);

            foreach (var (label, sql) in question.Mutants.Select(m => (m.Name, m.Sql)).Prepend(("solution", question.SolutionSql)))
            {
                ChangeOutcome outcome;
                ScriptRun actual;
                try
                {
                    outcome = await tracer.TraceAsync(name, setup, sql, ct);
                    actual = await runner.RunAsync(name, setup, ScriptAnalyzer.AnalyzeDml(sql).Batches, restricted: true, ct);
                }
                catch (QueryFailedException ex)
                {
                    // Some mutants are broken on purpose; the solutions never are.
                    if (label == "solution")
                        problems.Add($"{question.Id}: {ex.Message}");
                    continue;
                }

                // Only the last statement on each table says what it ends up holding.
                foreach (var change in outcome.Changes.GroupBy(c => c.Table, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
                {
                    var scene = ChangeSceneBuilder.Build(change);
                    Assert.NotEmpty(scene.Frames);
                    if (change.Note?.Contains("too many rows", StringComparison.Ordinal) == true || !actual.Snapshot.Tables.TryGetValue(change.Table, out var table))
                        continue;

                    // Values like GETDATE() differ between runs; the question's PresenceOnly columns list them.
                    var ignored = question.Compare.PresenceOnly.Where(p => p.StartsWith($"{change.Table}.", StringComparison.OrdinalIgnoreCase)).Select(p => p[(change.Table.Length + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var compare = change.Columns.Select((column, i) => (column, i)).Where(c => !ignored.Contains(c.column) && !IsNow(sql, c.column)).ToList();
                    var positions = compare.Select(c => table.Columns.ToList().IndexOf(c.column)).ToList();

                    var display = ResultComparer.Rows(table, table.Rows.Count).Rows;
                    var real = display.Select(row => string.Join('|', positions.Select(p => row[p]))).Order().ToList();
                    var shown = change.After.Select(row => string.Join('|', compare.Select(c => row.Cells[c.i]))).Order().ToList();
                    compared++;

                    if (!real.SequenceEqual(shown))
                        problems.Add($"{question.Id} {label}: {change.Table} ended on {shown.Count} rows, it really has {real.Count}. " +
                                     string.Join(" / ", shown.Except(real).Take(2).Concat(real.Except(shown).Take(2))));
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(compared > 30, $"Only {compared} tables were compared.");
    }

    /// <summary>A column set to GETDATE() or SYSDATETIME() has a different value every run.</summary>
    private static bool IsNow(string sql, string column) =>
        sql.Contains("GETDATE", StringComparison.OrdinalIgnoreCase) && column.Contains("Date", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task UPDATE_says_which_rows_changed_and_how()
    {
        var change = await OnlyChangeAsync(AP, "UPDATE Terms SET TermsDueDays = 99 WHERE TermsDueDays > 30 AND TermsID < 5;");

        Assert.Equal(ChangeKind.Update, change.Kind);
        Assert.Equal(["TermsDueDays > 30", "TermsID < 5"], change.Filter!.Conditions);
        Assert.Equal(change.Filter.Passes, change.Hits);
        Assert.Contains(change.Hits, hit => hit);
        Assert.Contains(change.Hits, hit => !hit);

        var dueDays = change.Columns.ToList().IndexOf("TermsDueDays");
        foreach (var row in change.After)
        {
            Assert.Equal(change.Hits[row.Before] ? [dueDays] : [], row.Changed);
            Assert.Equal(change.Hits[row.Before] ? "99" : change.Before[row.Before][dueDays], row.Cells[dueDays]);
        }
    }

    [Fact]
    public async Task It_works_on_a_table_without_a_primary_key()
    {
        var change = await OnlyChangeAsync(AP, "UPDATE TermsCopy SET TermsDueDays = 99 WHERE TermsID = 2;", "SELECT * INTO TermsCopy FROM Terms;");

        Assert.Equal(1, change.Hits.Count(hit => hit));
    }

    [Fact]
    public async Task Identical_rows_are_counted_correctly()
    {
        var change = await OnlyChangeAsync(AP, "DELETE TOP (1) TermsCopy WHERE TermsDueDays > 0;",
            "SELECT TermsDescription, TermsDueDays INTO TermsCopy FROM Terms WHERE TermsID = 1; INSERT TermsCopy SELECT * FROM TermsCopy;");

        Assert.Equal(2, change.Before.Count);
        Assert.Equal(1, change.Hits.Count(hit => hit));
        Assert.Single(change.After);
    }

    [Fact]
    public async Task UPDATE_with_a_join_shows_what_it_changed_without_cards()
    {
        var change = await OnlyChangeAsync(AP, """
            UPDATE i
            SET TermsID = 1
            FROM Invoices i
                JOIN Vendors v ON i.VendorID = v.VendorID
            WHERE v.VendorState = 'CA';
            """);

        Assert.Equal("Invoices", change.Table);
        Assert.Null(change.Filter);
        Assert.Contains(change.Hits, hit => hit);
    }

    [Fact]
    public async Task A_subquery_in_WHERE_is_tested_on_each_row()
    {
        var change = await OnlyChangeAsync(AP, "UPDATE Invoices SET TermsID = 1 WHERE VendorID IN (SELECT VendorID FROM Vendors WHERE VendorState = 'CA');");

        Assert.Equal(change.Filter!.Passes, change.Hits);
    }

    [Fact]
    public async Task DELETE_leaves_out_the_rows_it_deleted()
    {
        var change = await OnlyChangeAsync(AP, "DELETE ContactUpdates WHERE VendorID > 0;");

        Assert.Equal(ChangeKind.Delete, change.Kind);
        Assert.Equal(change.Before.Count - change.Hits.Count(hit => hit), change.After.Count);
    }

    [Fact]
    public async Task INSERT_reports_the_values_SQL_Server_filled_in()
    {
        var change = await OnlyChangeAsync(Guitars, "INSERT INTO Categories (CategoryName) VALUES ('Brass');");

        Assert.Equal(["CategoryID"], change.Filled);
        var added = change.After[^1];
        Assert.Equal(-1, added.Before);
        Assert.Contains("Brass", added.Cells);
        var id = change.Columns.ToList().IndexOf("CategoryID");
        Assert.Equal((change.Before.Max(r => int.Parse(r[id])) + 1).ToString(), added.Cells[id]);
    }

    [Fact]
    public async Task Rows_are_shown_in_the_order_of_their_first_column()
    {
        // The server reads Categories through its index on CategoryName, which isn't the order students expect.
        var change = await OnlyChangeAsync(Guitars, "INSERT INTO Categories (CategoryName) VALUES ('Brass');");

        var ids = change.Before.Select(r => int.Parse(r[0])).ToList();
        Assert.Equal(ids.Order(), ids);
    }

    [Fact]
    public async Task INSERT_SELECT_adds_a_row_for_each_row_it_selects()
    {
        var change = await OnlyChangeAsync(Guitars, "INSERT INTO Categories (CategoryName) SELECT 'Copy of ' + CategoryName FROM Categories;");

        Assert.Equal(change.Before.Count * 2, change.After.Count);
    }

    [Fact]
    public async Task Wide_tables_show_the_columns_that_matter()
    {
        var change = await OnlyChangeAsync(AP, "UPDATE Vendors SET DefaultTermsID = 1 WHERE VendorState = 'CA';");

        Assert.Equal(["VendorID", "VendorName", "VendorState", "DefaultTermsID"], change.Columns);
        Assert.StartsWith("Showing 4 of the 12 columns in Vendors", change.Note);
    }

    [Fact]
    public async Task Statements_see_what_earlier_ones_did()
    {
        var outcome = await TraceAsync(Guitars, """
            INSERT INTO Categories (CategoryName) VALUES ('Brass');

            UPDATE Categories SET CategoryName = 'Woodwinds' WHERE CategoryName = 'Brass';
            GO
            DELETE Categories WHERE CategoryName = 'Woodwinds';
            """);

        Assert.Equal([ChangeKind.Insert, ChangeKind.Update, ChangeKind.Delete], outcome.Changes.Select(c => c.Kind));
        Assert.Equal(1, outcome.Changes[1].Hits.Count(hit => hit));
        Assert.Equal(1, outcome.Changes[2].Hits.Count(hit => hit));
    }

    [Fact]
    public async Task Variables_still_work()
    {
        var change = await OnlyChangeAsync(AP, "DECLARE @Days int = 77;\nUPDATE Terms SET TermsDueDays = @Days WHERE TermsID = 1;");

        Assert.Equal("77", change.After[0].Cells[change.Columns.ToList().IndexOf("TermsDueDays")]);
    }

    [Fact]
    public async Task The_students_own_SELECTs_dont_get_in_the_way()
    {
        var change = await OnlyChangeAsync(AP, "SELECT * FROM Terms;\nDELETE ContactUpdates WHERE VendorID > 0;\nSELECT * FROM ContactUpdates;");

        Assert.Equal(ChangeKind.Delete, change.Kind);
    }

    [Fact]
    public async Task Nothing_stays_changed()
    {
        var before = await CountAsync(Guitars, "Categories");

        await OnlyChangeAsync(Guitars, "INSERT INTO Categories (CategoryName) VALUES ('Brass');");
        await OnlyChangeAsync(AP, "UPDATE Terms SET TermsDueDays = 12345;");

        Assert.Equal(before, await CountAsync(Guitars, "Categories"));
        Assert.Equal(0, await CountAsync(AP, "Terms WHERE TermsDueDays = 12345"));
    }

    [Fact]
    public async Task IDs_dont_creep_up_between_runs()
    {
        var first = await OnlyChangeAsync(Guitars, "INSERT INTO Categories (CategoryName) VALUES ('Brass');");
        var second = await OnlyChangeAsync(Guitars, "INSERT INTO Categories (CategoryName) VALUES ('Brass');");

        Assert.Equal(first.After[^1].Cells, second.After[^1].Cells);
    }

    [Fact]
    public async Task A_script_that_ends_the_transaction_says_so()
    {
        var outcome = await TraceAsync(AP, "UPDATE Terms SET TermsDueDays = 12345;\nROLLBACK;");

        Assert.True(outcome.EndedTransaction);
        Assert.Single(outcome.Changes);
    }

    [Fact]
    public async Task A_failed_statement_is_reported_with_its_line_number()
    {
        var failure = await Assert.ThrowsAsync<QueryFailedException>(() => TraceAsync(Guitars, "\nDELETE Categories WHERE CategoryID = 1;"));

        Assert.Contains("Line 2", failure.Message);
        Assert.Contains("REFERENCE", failure.Message);
    }

    [Fact]
    public async Task It_runs_as_the_restricted_user()
    {
        var change = await OnlyChangeAsync(AP, "UPDATE Terms SET TermsDescription = CURRENT_USER WHERE TermsID = 1;");

        Assert.Equal(DatabaseManager.WriterUser, change.After[0].Cells[change.Columns.ToList().IndexOf("TermsDescription")]);
    }

    [Fact]
    public async Task A_script_with_nothing_to_show_says_so()
    {
        var outcome = await TraceAsync(AP, "SELECT * INTO TermsCopy FROM Terms;");

        Assert.Empty(outcome.Changes);
        Assert.StartsWith("The visualizer shows INSERT, UPDATE and DELETE statements", Assert.Single(outcome.Problems));
    }
}
