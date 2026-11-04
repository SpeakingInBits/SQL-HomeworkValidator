using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Execution;
using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Traces summary queries on a real SQL Server. <see cref="VisualizerServerTests"/> checks that every homework solution
/// ends on its real result; these check how the rows got there. Needs HV_TEST_CONNECTION_STRING and the validator's databases.
/// </summary>
[Trait("Category", "NeedsSqlServer")]
[Collection(SqlServerCollection.Name)]
public class VisualizerGroupServerTests
{
    private const string AP = "HV_AP_base";

    private static ServerConnection Server()
    {
        var connectionString = Environment.GetEnvironmentVariable("HV_TEST_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "HV_TEST_CONNECTION_STRING isn't set.");
        return new ServerConnection(connectionString!);
    }

    private static async Task<QueryTrace> TraceAsync(string sql)
    {
        var outcome = await new QueryTracer(Server()).TraceAsync(AP, sql, TestContext.Current.CancellationToken);
        Assert.Empty(outcome.Problems);
        return outcome.Trace!;
    }

    private static List<string> FinalRows(QueryTrace trace) => trace.Result
        .Where((_, i) => trace.Cuts.Count == 0 || trace.Cuts[^1].Keeps[i])
        .Select(r => string.Join('|', trace.ResultCells(r)))
        .ToList();

    private static async Task<List<string>> ActualRowsAsync(string sql)
    {
        var table = await new QueryRunner(Server()).RunAsync(AP, sql, restricted: true, TestContext.Current.CancellationToken);
        return ResultComparer.Rows(table, table.Rows.Count).Rows.Select(r => string.Join('|', r)).ToList();
    }

    [Fact]
    public async Task Each_row_goes_into_the_group_with_its_key()
    {
        var trace = await TraceAsync("SELECT VendorState, COUNT(*) AS Vendors FROM Vendors GROUP BY VendorState");

        var grouping = trace.Grouping!;
        Assert.Equal(trace.Rows.Count, grouping.GroupOfRow.Count(g => g >= 0));
        for (var i = 0; i < trace.Rows.Count; i++)
            Assert.Equal(trace.Rows[i][0], grouping.Labels[grouping.GroupOfRow[i]]);

        // Each group's row counts exactly the rows in it.
        for (var r = 0; r < grouping.Rows.Count; r++)
        {
            var group = grouping.Sources[r];
            Assert.Equal(grouping.Labels[group], grouping.Rows[r][0]);
            Assert.Equal(grouping.GroupOfRow.Count(g => g == group).ToString(), grouping.Rows[r][1]);
        }
    }

    [Fact]
    public async Task Rows_show_the_key_and_what_the_aggregates_read()
    {
        var trace = await TraceAsync("SELECT TermsID, SUM(InvoiceTotal - CreditTotal) AS Owed, MAX(InvoiceDate) AS Latest FROM Invoices GROUP BY TermsID");

        Assert.Equal(["TermsID", "InvoiceTotal - CreditTotal", "InvoiceDate"], trace.Columns);
        Assert.Equal(["TermsID", "Owed", "Latest"], trace.Grouping!.Columns);
        Assert.Equal([1, 2], trace.Grouping.Calculated);
    }

    [Fact]
    public async Task WHERE_runs_before_the_groups_form()
    {
        var trace = await TraceAsync("SELECT TermsID, COUNT(*) AS Invoices FROM Invoices WHERE InvoiceTotal > 500 GROUP BY TermsID");

        var grouping = trace.Grouping!;
        for (var i = 0; i < trace.Rows.Count; i++)
            Assert.Equal(trace.Filter!.Passes[i], grouping.GroupOfRow[i] >= 0);
        Assert.Contains(trace.Filter!.Passes, passes => !passes);
    }

    [Fact]
    public async Task A_group_that_only_rows_failing_WHERE_belong_to_doesnt_exist()
    {
        // Group numbers are counted among the rows that pass, so groups stay lined up with their rows.
        var trace = await TraceAsync("SELECT TermsID, COUNT(*) AS Invoices FROM Invoices WHERE TermsID > 1 GROUP BY TermsID");

        var grouping = trace.Grouping!;
        Assert.DoesNotContain("1", grouping.Labels);
        Assert.All(grouping.Rows, (row, r) => Assert.Equal(grouping.Labels[grouping.Sources[r]], row[0]));
    }

    [Fact]
    public async Task HAVING_tests_the_groups()
    {
        const string sql = "SELECT VendorState, COUNT(*) AS Vendors FROM Vendors GROUP BY VendorState HAVING COUNT(*) > 2 AND MAX(VendorCity) > 'A' ORDER BY Vendors DESC, VendorState";

        var trace = await TraceAsync(sql);

        var having = trace.Grouping!.Having!;
        Assert.Equal(["COUNT(*) > 2", "MAX(VendorCity) > 'A'"], having.Conditions);
        for (var r = 0; r < trace.Grouping.Rows.Count; r++)
            Assert.Equal(int.Parse(trace.Grouping.Rows[r][1]) > 2, having.ConditionResults[0][r]);
        Assert.Contains(having.Passes, passes => !passes);
        Assert.Equal(await ActualRowsAsync(sql), FinalRows(trace));
    }

    [Fact]
    public async Task An_aggregate_over_all_the_rows_is_one_group()
    {
        var trace = await TraceAsync("SELECT COUNT(*) AS Vendors FROM Vendors");

        var grouping = trace.Grouping!;
        Assert.Equal(["All rows"], grouping.Labels);
        Assert.Equal(["Row"], trace.Columns);
        Assert.All(grouping.GroupOfRow, g => Assert.Equal(0, g));
        Assert.Equal(trace.Rows.Count.ToString(), Assert.Single(grouping.Rows)[0]);
    }

    [Fact]
    public async Task An_aggregate_over_no_rows_still_returns_a_row()
    {
        const string sql = "SELECT COUNT(*) AS Vendors FROM Vendors WHERE VendorID < 0";

        var trace = await TraceAsync(sql);

        Assert.Equal("0", Assert.Single(trace.Grouping!.Rows)[0]);
        Assert.Equal(await ActualRowsAsync(sql), FinalRows(trace));
        Assert.NotEmpty(SceneBuilder.Build(trace).Frames);
    }

    [Fact]
    public async Task NULLs_form_a_group_of_their_own()
    {
        var trace = await TraceAsync("SELECT PaymentDate, COUNT(*) AS Invoices FROM Invoices GROUP BY PaymentDate");

        var grouping = trace.Grouping!;
        Assert.Contains("NULL", grouping.Labels);
        var nulls = grouping.Labels.ToList().IndexOf("NULL");
        Assert.All(trace.Rows.Where((_, i) => grouping.GroupOfRow[i] == nulls), row => Assert.Equal("NULL", row[0]));
    }

    [Fact]
    public async Task Several_keys_make_one_group_per_combination()
    {
        var trace = await TraceAsync("SELECT VendorState, VendorCity, COUNT(*) AS Vendors FROM Vendors GROUP BY VendorState, VendorCity");

        var grouping = trace.Grouping!;
        Assert.All(grouping.Rows, (row, r) => Assert.Equal($"{row[0]}, {row[1]}", grouping.Labels[grouping.Sources[r]]));
    }

    [Fact]
    public async Task ROLLUP_adds_summary_rows_after_the_groups()
    {
        const string sql = "SELECT TermsID, SUM(InvoiceTotal) AS Total FROM Invoices GROUP BY TermsID WITH ROLLUP";

        var trace = await TraceAsync(sql);

        var grouping = trace.Grouping!;
        Assert.Equal("WITH ROLLUP", grouping.SummaryRows);
        Assert.Equal(-1, grouping.Sources[^1]);
        Assert.Equal("NULL", grouping.Rows[^1][0]);
        Assert.Equal(grouping.Rows.Count - 1, grouping.Labels.Count);
        Assert.Equal((await ActualRowsAsync(sql)).Order(), FinalRows(trace).Order());
        Assert.Contains(SceneBuilder.Build(trace).Frames, f => f.Phase.StartsWith("WITH ROLLUP adds a row that totals several groups: Total = ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ROLLUP_on_two_keys_keeps_groups_lined_up_with_their_rows()
    {
        const string sql = "SELECT VendorState, VendorCity, COUNT(*) AS Vendors FROM Vendors GROUP BY ROLLUP (VendorState, VendorCity)";

        var trace = await TraceAsync(sql);

        var grouping = trace.Grouping!;
        foreach (var (row, r) in grouping.Rows.Select((row, r) => (row, r)).Where(x => grouping.Sources[x.r] >= 0))
            Assert.Equal(grouping.GroupOfRow.Count(g => g == grouping.Sources[r]).ToString(), row[2]);
        Assert.Equal((await ActualRowsAsync(sql)).Order(), FinalRows(trace).Order());
    }

    [Fact]
    public async Task TOP_keeps_the_first_groups_after_sorting()
    {
        const string sql = "SELECT TOP 3 VendorState, COUNT(*) AS Vendors FROM Vendors GROUP BY VendorState ORDER BY Vendors DESC, VendorState";

        var trace = await TraceAsync(sql);

        Assert.Equal(await ActualRowsAsync(sql), FinalRows(trace));
    }

    [Fact]
    public async Task A_window_function_keeps_every_row()
    {
        const string sql = "SELECT VendorID, InvoiceTotal, SUM(InvoiceTotal) OVER (PARTITION BY VendorID) AS VendorTotal FROM Invoices";

        var trace = await TraceAsync(sql);

        var grouping = trace.Grouping!;
        Assert.False(grouping.Collapses);
        Assert.Equal(trace.Rows.Count, grouping.Rows.Count);
        for (var r = 0; r < grouping.Rows.Count; r++)
            Assert.Equal(grouping.Labels[grouping.GroupOfRow[grouping.Sources[r]]], grouping.Rows[r][0]);
        Assert.Equal((await ActualRowsAsync(sql)).Order(), FinalRows(trace).Order());
    }

    [Fact]
    public async Task A_window_function_only_sees_the_rows_WHERE_kept()
    {
        const string sql = "SELECT InvoiceID, COUNT(*) OVER () AS Kept FROM Invoices WHERE InvoiceTotal > 1000 ORDER BY InvoiceID";

        var trace = await TraceAsync(sql);

        Assert.Contains(trace.Filter!.Passes, passes => !passes);
        Assert.Equal(await ActualRowsAsync(sql), FinalRows(trace));
    }

    [Fact]
    public async Task Different_partitions_get_a_note()
    {
        var trace = await TraceAsync("SELECT SUM(InvoiceTotal) OVER (PARTITION BY VendorID) AS A, SUM(InvoiceTotal) OVER (PARTITION BY TermsID) AS B FROM Invoices");

        Assert.Contains("different partitions", trace.Note);
    }
}
