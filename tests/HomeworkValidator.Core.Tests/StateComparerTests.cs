using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Tests;

public class StateComparerTests
{
    private static DatabaseSnapshot Snapshot(params (string Name, ResultTable Table)[] tables) =>
        new(tables.ToDictionary(t => t.Name, t => t.Table, StringComparer.OrdinalIgnoreCase));

    private static ResultTable Categories(params object?[][] rows) => new(["CategoryID", "CategoryName"], rows);

    [Fact]
    public void Same_tables_and_rows_pass()
    {
        var expected = Snapshot(("Categories", Categories([1, "Guitars"], [5, "Brass"])));
        var actual = Snapshot(("Categories", Categories([5, "Brass"], [1, "Guitars"])));

        Assert.True(StateComparer.Compare(expected, actual, CompareOptions.Default).Passed);
    }

    [Fact]
    public void Differences_name_the_table()
    {
        var expected = Snapshot(("Categories", Categories([1, "Guitars"], [5, "Brass"])));
        var actual = Snapshot(("Categories", Categories([1, "Guitars"])));

        var result = StateComparer.Compare(expected, actual, CompareOptions.Default);

        Assert.False(result.Passed);
        Assert.Equal("Categories", result.Table);
        Assert.Contains("Expected Categories to have 2 rows, but it has 1 row.", result.Message);
        Assert.Equal("Brass", result.Missing!.Rows[0][1]);
        Assert.False(result.Structural);
    }

    [Fact]
    public void The_first_differing_table_comes_back_whole_when_asked()
    {
        var expected = Snapshot(("Categories", Categories([1, "Guitars"], [5, "Brass"])));
        var actual = Snapshot(("Categories", Categories([1, "Guitars"], [5, "brass instruments"], [6, null])));

        var result = StateComparer.Compare(expected, actual, CompareOptions.Default, fullRows: 10);

        Assert.Equal(["CategoryID", "CategoryName"], result.Actual!.Columns);
        Assert.Equal(3, result.Actual.Total);
        Assert.Equal(["6", "NULL"], result.Actual.Rows[2]);
        Assert.Equal(2, result.Expected!.Total);
        Assert.Null(StateComparer.Compare(expected, actual, CompareOptions.Default).Actual);
    }

    [Fact]
    public void A_missing_table_is_reported()
    {
        var expected = Snapshot(("VendorCopy", Categories()), ("InvoiceCopy", Categories()));
        var actual = Snapshot(("VendorCopy", Categories()));

        var result = StateComparer.Compare(expected, actual, CompareOptions.Default);

        Assert.False(result.Passed);
        Assert.Contains("Expected table InvoiceCopy to exist", result.Message);
        Assert.True(result.Structural);
    }

    [Fact]
    public void A_table_left_behind_is_reported()
    {
        var expected = Snapshot(("Vendors", Categories()));
        var actual = Snapshot(("Vendors", Categories()), ("Scratch", Categories()));

        var result = StateComparer.Compare(expected, actual, CompareOptions.Default);

        Assert.False(result.Passed);
        Assert.Contains("table Scratch", result.Message);
        Assert.True(result.Structural);
    }

    [Fact]
    public void Other_changed_tables_are_listed()
    {
        var expected = Snapshot(("A", Categories([1, "x"])), ("B", Categories([1, "x"])), ("C", Categories([1, "x"])));
        var actual = Snapshot(("A", Categories([1, "y"])), ("B", Categories([1, "y"])), ("C", Categories([1, "y"])));

        var result = StateComparer.Compare(expected, actual, CompareOptions.Default);

        Assert.Equal("A", result.Table);
        Assert.Contains("Also different: tables B and C.", result.Message);
    }

    [Fact]
    public void Presence_only_columns_compare_set_or_null_but_not_the_value()
    {
        var options = CompareOptions.Default with { PresenceOnly = ["Products.DateAdded"] };
        ResultTable Products(object? date) => new(["ProductID", "DateAdded"], [[11, date]]);

        var expected = Snapshot(("Products", Products(new DateTime(2026, 10, 29, 9, 0, 0))));
        var later = Snapshot(("Products", Products(new DateTime(2026, 10, 29, 9, 0, 3))));
        var missing = Snapshot(("Products", Products(null)));

        Assert.True(StateComparer.Compare(expected, later, options).Passed);
        Assert.False(StateComparer.Compare(expected, missing, options).Passed);
    }
}
