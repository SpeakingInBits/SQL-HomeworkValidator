using System.Text.RegularExpressions;
using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Tests;

public class ResultComparerTests
{
    private static ResultTable Table(string[] columns, params object?[][] rows) => new(columns, rows);

    private static CompareOptions Unordered => CompareOptions.Default;

    private static CompareOptions OrderedBy(params string[] columns) =>
        CompareOptions.Default with { Order = columns.Select(c => new OrderKey(c, null)).ToList() };

    [Fact]
    public void Same_rows_in_any_order_pass_when_unordered()
    {
        var expected = Table(["A"], [1], [2], [3]);
        var actual = Table(["A"], [3], [1], [2]);

        Assert.True(ResultComparer.Compare(expected, actual, Unordered).Passed);
    }

    [Fact]
    public void Duplicate_rows_count()
    {
        var expected = Table(["A"], [1], [1], [2]);
        var actual = Table(["A"], [1], [2], [2]);

        var result = ResultComparer.Compare(expected, actual, Unordered);

        Assert.False(result.Passed);
        Assert.Equal(1, result.Missing!.Total);
        Assert.Equal(1, result.Unexpected!.Total);
    }

    [Fact]
    public void Numbers_of_different_types_and_scales_are_equal()
    {
        var expected = Table(["A", "B", "C"], [10, 10.5m, 7.0m]);
        var actual = Table(["A", "B", "C"], [10.0000m, 10.50m, 7d]);

        Assert.True(ResultComparer.Compare(expected, actual, Unordered).Passed);
    }

    [Fact]
    public void Decimals_option_rounds_before_comparing()
    {
        var expected = Table(["A"], [186.1962m]);
        var actual = Table(["A"], [186.196200m + 0.0004m]);

        Assert.False(ResultComparer.Compare(expected, actual, Unordered).Passed);
        Assert.True(ResultComparer.Compare(expected, actual, Unordered with { Decimals = 2 }).Passed);
    }

    [Fact]
    public void Null_is_not_the_text_null()
    {
        var expected = Table(["A"], new object?[] { null });
        var actual = Table(["A"], ["NULL"]);

        Assert.False(ResultComparer.Compare(expected, actual, Unordered).Passed);
    }

    [Fact]
    public void Date_matches_midnight_datetime()
    {
        var expected = Table(["D"], [new DateTime(2022, 10, 8)]);
        var actual = Table(["D"], [new DateTime(2022, 10, 8, 0, 0, 0)]);

        Assert.True(ResultComparer.Compare(expected, actual, Unordered).Passed);
    }

    [Fact]
    public void Columns_by_name_ignore_order_and_case()
    {
        var expected = Table(["Number", "Total"], ["A1", 5]);
        var actual = Table(["total", "NUMBER"], [5, "A1"]);

        Assert.True(ResultComparer.Compare(expected, actual, Unordered).Passed);
    }

    [Fact]
    public void Missing_and_extra_columns_are_named()
    {
        var expected = Table(["Number", "Total"], ["A1", 5]);
        var actual = Table(["Number", "InvoiceTotal"], ["A1", 5]);

        var result = ResultComparer.Compare(expected, actual, Unordered);

        Assert.False(result.Passed);
        Assert.Contains("Missing: Total", result.Message);
        Assert.Contains("Not expected: InvoiceTotal", result.Message);
    }

    [Fact]
    public void Unnamed_columns_ask_for_an_alias()
    {
        var expected = Table(["Full Name"], ["Doe, John"]);
        var actual = Table([""], ["Doe, John"]);

        var result = ResultComparer.Compare(expected, actual, Unordered);

        Assert.False(result.Passed);
        Assert.Contains("no name", result.Message);
    }

    [Fact]
    public void Columns_by_position_ignore_names()
    {
        var expected = Table(["A", "B"], [1, 2]);
        var actual = Table(["X", "Y"], [1, 2]);

        Assert.True(ResultComparer.Compare(expected, actual, Unordered with { Columns = ColumnMatch.ByPosition }).Passed);
    }

    [Fact]
    public void Row_count_mode_only_compares_counts()
    {
        var expected = Table(["A"], [1], [2]);
        var actual = Table(["X", "Y"], ["a", "b"], ["c", "d"]);

        Assert.True(ResultComparer.Compare(expected, actual, Unordered with { Columns = ColumnMatch.RowCount }).Passed);
    }

    [Fact]
    public void Wrong_order_fails_when_ordered()
    {
        var expected = Table(["Price"], [300], [200], [100]);
        var actual = Table(["Price"], [100], [200], [300]);

        var result = ResultComparer.Compare(expected, actual, OrderedBy("Price"));

        Assert.False(result.Passed);
        Assert.Contains("aren't in the right order", result.Message);
    }

    [Fact]
    public void Tied_rows_may_come_in_any_order()
    {
        // Two products share a price of 799.99; either order is a correct ORDER BY ListPrice DESC.
        var expected = Table(["Name", "Price"], ["Tama", 799.99m], ["Precision", 799.99m], ["Strat", 699m]);
        var actual = Table(["Name", "Price"], ["Precision", 799.99m], ["Tama", 799.99m], ["Strat", 699m]);

        Assert.True(ResultComparer.Compare(expected, actual, OrderedBy("Price")).Passed);
    }

    [Fact]
    public void Key_pattern_sorts_on_part_of_a_column()
    {
        // Sorted by last name only: two Smiths may come in either order.
        var options = CompareOptions.Default with { Order = [new OrderKey("FullName", new Regex("^[^,]*"))] };
        var expected = Table(["FullName"], ["Smith, Ann"], ["Smith, Bob"], ["Young, Cy"]);
        var tiesSwapped = Table(["FullName"], ["Smith, Bob"], ["Smith, Ann"], ["Young, Cy"]);
        var wrong = Table(["FullName"], ["Young, Cy"], ["Smith, Ann"], ["Smith, Bob"]);

        Assert.True(ResultComparer.Compare(expected, tiesSwapped, options).Passed);
        Assert.False(ResultComparer.Compare(expected, wrong, options).Passed);
    }

    [Fact]
    public void Order_key_must_be_a_result_column()
    {
        var expected = Table(["A"], [1]);

        Assert.Throws<ContentException>(() => ResultComparer.Compare(expected, expected, OrderedBy("Missing")));
    }
}
