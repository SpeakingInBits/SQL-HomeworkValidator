using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Traces queries on a real SQL Server, and checks that what the animation ends on is what the query really returns.
/// Runs only when HV_TEST_CONNECTION_STRING is set, and needs the validator's databases.
/// </summary>
[Trait("Category", "NeedsSqlServer")]
public class VisualizerServerTests
{
    private const string AP = "HV_AP_base";

    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    private static ServerConnection Server()
    {
        var connectionString = Environment.GetEnvironmentVariable("HV_TEST_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "HV_TEST_CONNECTION_STRING isn't set.");
        return new ServerConnection(connectionString!);
    }

    private static async Task<QueryTrace> TraceAsync(string sql, string database = AP)
    {
        var outcome = await new QueryTracer(Server()).TraceAsync(database, sql, TestContext.Current.CancellationToken);
        Assert.Empty(outcome.Problems);
        return outcome.Trace!;
    }

    /// <summary>The rows the animation ends on, without the columns added to explain it.</summary>
    private static List<string> FinalRows(QueryTrace trace)
    {
        var result = trace.Result;
        return result
            .Where((_, i) => trace.Cuts.Count == 0 || trace.Cuts[^1].Keeps[i])
            .Select(r => string.Join('|', trace.Rows[r].Take(trace.ResultColumns)))
            .ToList();
    }

    private static async Task<List<string>> ActualRowsAsync(string database, string sql)
    {
        var table = await new QueryRunner(Server()).RunAsync(database, sql, restricted: true, TestContext.Current.CancellationToken);
        return ResultComparer.Rows(table, table.Rows.Count).Rows.Select(r => string.Join('|', r)).ToList();
    }

    [Fact]
    public async Task Every_solution_it_can_show_ends_on_the_querys_real_result()
    {
        var server = Server();
        var tracer = new QueryTracer(server);
        var traced = 0;
        var problems = new List<string>();

        foreach (var question in Content.Assignments.SelectMany(a => a.Questions).Where(q => !q.IsScript))
        {
            var database = Content.GetDatabase(question.Database);
            var name = DatabaseManager.DatabaseName(database, database.GetDataset(question.Datasets[0]));
            foreach (var (label, sql) in question.Mutants.Select(m => (m.Name, m.Sql)).Prepend(("solution", question.SolutionSql)))
            {
                if (QueryShape.Analyze(sql).Problems.Count > 0)
                    continue;

                try
                {
                    var outcome = await tracer.TraceAsync(name, sql, TestContext.Current.CancellationToken);
                    var trace = outcome.Trace!;
                    var scene = SceneBuilder.Build(trace);
                    Assert.NotEmpty(scene.Frames);
                    traced++;

                    if (trace.Note is not null)
                        continue;

                    var (actual, final) = (await ActualRowsAsync(name, QueryShape.Analyze(sql).Query), FinalRows(trace));
                    if (!actual.Order().SequenceEqual(final.Order()))
                        problems.Add($"{question.Id} {label}: ended on {final.Count} rows, the query returns {actual.Count}. First differences: " +
                                     string.Join(" / ", final.Except(actual).Take(2).Concat(actual.Except(final).Take(2))));
                }
                catch (QueryFailedException ex)
                {
                    // Some mutants are broken on purpose; the solutions never are.
                    if (label == "solution")
                        problems.Add($"{question.Id}: {ex.Message}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(traced > 50, $"Only {traced} queries were traced.");
    }

    [Fact]
    public async Task A_sorted_result_ends_in_the_querys_order()
    {
        const string sql = "SELECT VendorName, VendorCity FROM Vendors WHERE VendorState = 'CA' ORDER BY VendorCity DESC, VendorName";

        var trace = await TraceAsync(sql);

        Assert.Equal(await ActualRowsAsync(AP, sql), FinalRows(trace));
    }

    [Fact]
    public async Task WHERE_shows_each_condition_and_the_columns_it_reads()
    {
        var trace = await TraceAsync("SELECT VendorName FROM Vendors WHERE VendorState = 'CA' AND VendorCity LIKE 'S%'");

        Assert.Equal(["VendorName", "VendorState", "VendorCity"], trace.Columns);
        Assert.Equal(1, trace.ResultColumns);
        Assert.Equal(["VendorState = 'CA'", "VendorCity LIKE 'S%'"], trace.Filter!.Conditions);

        for (var i = 0; i < trace.Rows.Count; i++)
        {
            Assert.Equal(trace.Rows[i][1] == "CA", trace.Filter.ConditionResults[0][i]);
            Assert.Equal(trace.Filter.ConditionResults[0][i] && trace.Filter.ConditionResults[1][i], trace.Filter.Passes[i]);
        }
    }

    [Fact]
    public async Task NULL_makes_a_condition_false_as_WHERE_does()
    {
        var trace = await TraceAsync("SELECT InvoiceID, PaymentDate FROM Invoices WHERE PaymentDate > '2000-01-01'");

        Assert.Contains(trace.Rows, r => r[1] == "NULL");
        for (var i = 0; i < trace.Rows.Count; i++)
            Assert.Equal(trace.Rows[i][1] != "NULL", trace.Filter!.Passes[i]);
    }

    [Fact]
    public async Task A_join_knows_which_rows_formed_each_joined_row()
    {
        var trace = await TraceAsync("""
            SELECT t.TermsDescription, v.VendorName
            FROM Terms t
                JOIN Vendors v ON t.TermsID = v.DefaultTermsID
            """);

        Assert.Equal(2, trace.Sources.Count);
        var termsId = trace.Sources[0].Columns.ToList().IndexOf("TermsID");
        var terms = trace.Sources[1].Columns.ToList().IndexOf("DefaultTermsID");
        Assert.All(trace.Paths, path => Assert.Equal(trace.Sources[0].Rows[path[0]][termsId], trace.Sources[1].Rows[path[1]][terms]));
    }

    [Fact]
    public async Task An_outer_join_marks_the_rows_with_no_match()
    {
        var trace = await TraceAsync("""
            SELECT v.VendorName, i.InvoiceNumber
            FROM Vendors v
                LEFT JOIN Invoices i ON v.VendorID = i.VendorID
            """);

        var unmatched = trace.Paths.Select((p, i) => (p, i)).Where(x => x.p[1] == QueryTrace.NoMatch).ToList();
        Assert.NotEmpty(unmatched);
        Assert.All(unmatched, x => Assert.Equal("NULL", trace.Rows[x.i][1]));
    }

    [Fact]
    public async Task A_self_join_tells_the_two_sides_apart()
    {
        var trace = await TraceAsync("""
            SELECT a.VendorName, b.VendorName
            FROM Vendors a
                JOIN Vendors b ON a.VendorCity = b.VendorCity AND a.VendorID < b.VendorID
            """);

        Assert.Equal(["Vendors (a)", "Vendors (b)"], trace.Sources.Select(s => s.Label));
        Assert.NotEmpty(trace.Rows);
        Assert.All(trace.Paths, p => Assert.True(p[0] < p[1]));
    }

    [Fact]
    public async Task A_three_table_join_connects_each_table_to_the_one_its_ON_reads()
    {
        var trace = await TraceAsync("""
            SELECT v.VendorName, i.InvoiceNumber, t.TermsDescription
            FROM Invoices i
                JOIN Vendors v ON i.VendorID = v.VendorID
                JOIN Terms t ON i.TermsID = t.TermsID
            """);

        Assert.Equal([0, 0], trace.Joins.Select(j => j.LeftSource));
    }

    [Fact]
    public async Task The_in_depth_view_compares_every_pair_of_rows()
    {
        var trace = await TraceAsync("SELECT t.TermsID, v.VendorName FROM Terms t JOIN Vendors v ON t.TermsID = v.DefaultTermsID");

        var comparisons = trace.Comparisons!;
        Assert.Equal(trace.Sources[0].Rows.Count, comparisons.Length);
        Assert.Equal(trace.Rows.Count, comparisons.Sum(l => l.Count(hit => hit)));
        Assert.All(trace.Paths, p => Assert.True(comparisons[p[0]][p[1]]));
    }

    [Fact]
    public async Task A_comma_join_keeps_the_rows_that_pass_when_it_has_too_many()
    {
        const string sql = "SELECT v.VendorName, i.InvoiceNumber FROM Vendors v, Invoices i WHERE v.VendorID = i.VendorID";

        var trace = await TraceAsync(sql);

        // Every pair of rows is far more than can be shown, but all the matches still fit.
        Assert.Equal(QueryTracer.MaxRows, trace.Rows.Count);
        Assert.NotNull(trace.Note);
        Assert.Equal((await ActualRowsAsync(AP, sql)).Order(), FinalRows(trace).Order());
    }

    [Fact]
    public async Task TOP_keeps_the_rows_the_query_really_returns()
    {
        var plain = await TraceAsync("SELECT TOP 5 VendorName FROM Vendors ORDER BY VendorName");
        Assert.Equal("TOP 5 keeps the first 5 rows", Assert.Single(plain.Cuts).Label);
        Assert.Equal(5, FinalRows(plain).Count);

        const string percent = "SELECT TOP 10 PERCENT WITH TIES VendorState FROM Vendors ORDER BY VendorState";
        Assert.Equal(await ActualRowsAsync(AP, percent), FinalRows(await TraceAsync(percent)));
    }

    [Fact]
    public async Task DISTINCT_removes_the_duplicates()
    {
        const string sql = "SELECT DISTINCT VendorState FROM Vendors ORDER BY VendorState";

        var trace = await TraceAsync(sql);

        Assert.Equal("DISTINCT removes the duplicate rows", Assert.Single(trace.Cuts).Label);
        Assert.Equal(await ActualRowsAsync(AP, sql), FinalRows(trace));
    }

    [Fact]
    public async Task SELECT_star_doesnt_show_the_row_numbers()
    {
        var one = await TraceAsync("SELECT * FROM Terms");
        var two = await TraceAsync("SELECT * FROM Terms t JOIN Vendors v ON t.TermsID = v.DefaultTermsID");

        Assert.DoesNotContain(one.Columns, c => c.StartsWith("__hv", StringComparison.Ordinal));
        Assert.DoesNotContain(two.Columns, c => c.StartsWith("__hv", StringComparison.Ordinal));
        Assert.DoesNotContain(two.Sources.SelectMany(s => s.Columns), c => c.StartsWith("__hv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Calculated_columns_are_worked_out_by_the_server()
    {
        var trace = await TraceAsync("SELECT InvoiceTotal * 2 AS Doubled, InvoiceTotal + 1 FROM Invoices WHERE InvoiceTotal > 0");

        // WHERE reads InvoiceTotal, which isn't a column of the result, so it's shown after the ones that are.
        Assert.Equal(["Doubled", "(no name)", "InvoiceTotal"], trace.Columns);
        var row = trace.Rows[0].Select(decimal.Parse).ToList();
        Assert.Equal(row[2] * 2, row[0]);
        Assert.Equal(row[2] + 1, row[1]);
    }

    [Fact]
    public async Task A_mistake_in_the_query_is_reported_with_its_line_number()
    {
        var failure = await Assert.ThrowsAsync<QueryFailedException>(() => TraceAsync("SELECT Nope\nFROM Vendors"));

        Assert.Contains("Line 1", failure.Message);
        Assert.Contains("Nope", failure.Message);
    }

    [Fact]
    public async Task It_runs_as_the_read_only_user()
    {
        var trace = await TraceAsync("SELECT CURRENT_USER AS Who, TermsID FROM Terms");

        Assert.All(trace.Rows, r => Assert.Equal(DatabaseManager.ReaderUser, r[0]));
    }
}
