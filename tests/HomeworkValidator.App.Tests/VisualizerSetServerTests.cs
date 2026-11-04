using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Traces UNION, INTERSECT and EXCEPT on a real SQL Server, and checks that what the animation ends on is what the
/// query really returns. Needs HV_TEST_CONNECTION_STRING and the validator's databases.
/// </summary>
[Trait("Category", "NeedsSqlServer")]
[Collection(SqlServerCollection.Name)]
public class VisualizerSetServerTests
{
    private const string AP = "HV_AP_base";

    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    private static ServerConnection Server()
    {
        var connectionString = Environment.GetEnvironmentVariable("HV_TEST_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "HV_TEST_CONNECTION_STRING isn't set.");
        return new ServerConnection(connectionString!);
    }

    private static async Task<SetTrace> TraceAsync(string sql, string database = AP)
    {
        var shape = SetShape.Analyze(sql);
        Assert.NotNull(shape);
        var outcome = await new SetTracer(Server()).TraceAsync(database, shape, TestContext.Current.CancellationToken);
        Assert.Empty(outcome.Problems);
        return outcome.Trace!;
    }

    private static async Task<List<string>> ActualRowsAsync(string sql, string database = AP)
    {
        var table = await new QueryRunner(Server()).RunAsync(database, sql, restricted: true, TestContext.Current.CancellationToken);
        return ResultComparer.Rows(table, table.Rows.Count).Rows.Select(Line).ToList();
    }

    private static string Line(IReadOnlyList<string> row) => string.Join('|', row);

    /// <summary>Checks the animation ends on the query's real rows, in its real order, and that every row was accounted for.</summary>
    private static async Task EndsOnRealResultAsync(string sql)
    {
        var trace = await TraceAsync(sql);

        Assert.Equal(await ActualRowsAsync(sql), trace.Result.Select(Line));
        Assert.NotEmpty(SetSceneBuilder.Build(trace));
    }

    [Fact]
    public async Task Every_homework_query_ends_on_its_real_result()
    {
        var tracer = new SetTracer(Server());
        var traced = 0;
        var problems = new List<string>();

        foreach (var question in Content.Assignments.SelectMany(a => a.Questions).Where(q => !q.IsScript))
        {
            var database = Content.GetDatabase(question.Database);
            var name = DatabaseManager.DatabaseName(database, database.GetDataset(question.Datasets[0]));
            foreach (var (label, sql) in question.Mutants.Select(m => (m.Name, m.Sql)).Prepend(("solution", question.SolutionSql)))
            {
                if (SetShape.Analyze(sql) is not { } shape)
                    continue;

                var trace = (await tracer.TraceAsync(name, shape, TestContext.Current.CancellationToken)).Trace!;
                var scenes = SetSceneBuilder.Build(trace);
                traced++;

                // Each query has its own animation here, and the last one combines them.
                if (scenes.Count != trace.Branches.Count + 1)
                    problems.Add($"{question.Id} {label}: {scenes.Count} animations. {SetSceneBuilder.Note(trace)}");

                var (actual, final) = (await ActualRowsAsync(shape.Query, name), trace.Result.Select(Line).ToList());
                if (!actual.Order().SequenceEqual(final.Order()))
                    problems.Add($"{question.Id} {label}: ended on {final.Count} rows, the query returns {actual.Count}.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(traced >= 8, $"Only {traced} queries were traced.");
    }

    [Fact]
    public async Task UNION_removes_the_rows_both_queries_return()
    {
        const string sql = "SELECT VendorState FROM Vendors WHERE VendorState < 'D' UNION SELECT VendorState FROM Vendors WHERE VendorState < 'O' ORDER BY VendorState";

        await EndsOnRealResultAsync(sql);
        var trace = await TraceAsync(sql);

        var step = Assert.Single(trace.Steps);
        Assert.Contains(step.RightRows, r => r.Slot < 0);
        var result = trace.Tables[step.Result].Rows;
        Assert.Equal(result.Count, result.Select(Line).Distinct().Count());
    }

    [Fact]
    public async Task A_duplicate_within_one_query_is_removed_too()
    {
        var trace = await TraceAsync("SELECT VendorState FROM Vendors UNION SELECT 'ZZ'");

        var step = Assert.Single(trace.Steps);
        Assert.Contains(step.LeftRows, r => r.Slot < 0 && r.Related.Count == 1);
    }

    [Fact]
    public async Task UNION_ALL_keeps_every_row()
    {
        const string sql = "SELECT VendorState FROM Vendors UNION ALL SELECT VendorState FROM Vendors ORDER BY VendorState";

        await EndsOnRealResultAsync(sql);
        var step = Assert.Single((await TraceAsync(sql)).Steps);
        Assert.All(step.LeftRows.Concat(step.RightRows), r => Assert.True(r.Slot >= 0));
    }

    [Fact]
    public async Task INTERSECT_keeps_the_rows_in_both()
    {
        const string sql = "SELECT VendorID FROM Vendors INTERSECT SELECT VendorID FROM Invoices ORDER BY VendorID";

        await EndsOnRealResultAsync(sql);
        var trace = await TraceAsync(sql);

        var step = Assert.Single(trace.Steps);
        Assert.Contains(step.LeftRows, r => r.Slot < 0 && r.Related.Count == 0);
        Assert.Contains(step.LeftRows, r => r.Slot >= 0 && r.Related.Count > 0);
        Assert.Empty(step.RightRows);
    }

    [Fact]
    public async Task EXCEPT_removes_the_rows_in_the_second_query()
    {
        const string sql = "SELECT VendorID FROM Vendors EXCEPT SELECT VendorID FROM Invoices ORDER BY VendorID";

        await EndsOnRealResultAsync(sql);
        var step = Assert.Single((await TraceAsync(sql)).Steps);
        Assert.All(step.LeftRows, r => Assert.Equal(r.Slot < 0, r.Related.Count > 0));
    }

    [Fact]
    public async Task A_chain_ends_on_the_real_result()
    {
        await EndsOnRealResultAsync("SELECT VendorState FROM Vendors UNION SELECT 'ZZ' EXCEPT SELECT 'CA' ORDER BY VendorState");
    }

    [Fact]
    public async Task INTERSECT_goes_first_in_a_chain()
    {
        const string sql = "SELECT 'ZZ' AS State UNION SELECT VendorState FROM Vendors INTERSECT SELECT 'CA' ORDER BY State";

        await EndsOnRealResultAsync(sql);

        // Every row must be accounted for by the steps themselves. If INTERSECT ran second, ZZ wouldn't survive it.
        var trace = await TraceAsync(sql);
        Assert.Equal(["INTERSECT", "UNION"], trace.Steps.Select(s => s.Operator));
        Assert.Equal(["CA"], trace.Tables[trace.Steps[0].Result].Rows.Select(Line));
        Assert.Equal(["ZZ", "CA"], trace.Tables[trace.Steps[1].Result].Rows.Select(Line));
    }

    [Fact]
    public async Task A_result_bigger_than_the_row_limit_is_kept_whole()
    {
        const string sql = "SELECT VendorID FROM Vendors UNION ALL SELECT VendorID FROM Vendors ORDER BY VendorID";

        var trace = await TraceAsync(sql);

        Assert.True(trace.Result.Count > QueryTracer.MaxRows);
        Assert.False(trace.Truncated);
        Assert.Equal(await ActualRowsAsync(sql), trace.Result.Select(Line));
    }

    [Fact]
    public async Task Parentheses_change_the_result()
    {
        await EndsOnRealResultAsync("SELECT VendorState FROM Vendors EXCEPT (SELECT 'CA' UNION SELECT 'NY') ORDER BY VendorState");
    }

    [Fact]
    public async Task Case_doesnt_make_a_row_different_when_the_server_says_it_doesnt()
    {
        await EndsOnRealResultAsync("SELECT 'abc' AS Letters UNION SELECT 'ABC'");
    }

    [Fact]
    public async Task Values_of_different_types_can_still_be_duplicates()
    {
        await EndsOnRealResultAsync("SELECT 1 AS Number UNION SELECT 1.00");
    }

    [Fact]
    public async Task NULLs_count_as_duplicates_of_each_other()
    {
        await EndsOnRealResultAsync("SELECT PaymentDate FROM Invoices UNION SELECT NULL ORDER BY PaymentDate");
    }

    [Fact]
    public async Task The_result_is_named_by_the_first_query()
    {
        var trace = await TraceAsync("SELECT VendorState AS State FROM Vendors UNION SELECT VendorCity FROM Vendors");

        Assert.Equal(["State"], trace.Tables[trace.Steps[0].Result].Columns);
        Assert.Equal(["VendorCity"], trace.Tables[1].Columns);
    }

    [Fact]
    public async Task Each_query_gets_its_own_animation()
    {
        var trace = await TraceAsync("""
            SELECT v.VendorName, 'Paid' AS Status
            FROM Vendors v JOIN Invoices i ON v.VendorID = i.VendorID
            WHERE i.PaymentDate IS NOT NULL
            UNION
            SELECT VendorName, 'New'
            FROM Vendors
            WHERE VendorState = 'CA'
            ORDER BY VendorName;
            """);

        var scenes = SetSceneBuilder.Build(trace);

        Assert.Equal(["Query 1: Vendors ⋈ Invoices → WHERE", "Query 2: Vendors → WHERE", "Combine: UNION → ORDER BY"], scenes.Select(s => s.Title));
    }

    [Fact]
    public async Task A_query_that_cant_be_shown_alone_can_still_be_combined()
    {
        var trace = await TraceAsync("SELECT VendorState FROM (SELECT * FROM Vendors) v UNION SELECT VendorCity FROM Vendors");

        var scenes = SetSceneBuilder.Build(trace);

        Assert.Equal(["Query 2", "Combine"], scenes.Select(s => s.Title.Split(':')[0]));
        Assert.Contains("Query 1 has no animation of its own", SetSceneBuilder.Note(trace));
    }

    [Fact]
    public async Task A_mistake_in_the_query_is_reported_with_its_line_number()
    {
        var failure = await Assert.ThrowsAsync<QueryFailedException>(() => TraceAsync("SELECT Nope FROM Vendors\nUNION\nSELECT VendorName FROM Vendors"));

        Assert.Contains("Line 1", failure.Message);
    }

    [Fact]
    public async Task Queries_that_dont_line_up_are_reported()
    {
        var failure = await Assert.ThrowsAsync<QueryFailedException>(() => TraceAsync("SELECT VendorID, VendorName FROM Vendors UNION SELECT VendorID FROM Vendors"));

        Assert.Contains("equal number", failure.Message);
    }
}
