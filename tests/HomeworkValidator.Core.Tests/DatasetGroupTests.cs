using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Tests;

public class DatasetGroupTests
{
    private const string EdgeHint = "Check products priced exactly 500 and exactly 2000.";
    private const string ShiftedHint = "The shifted data has different ID values.";

    private static ResultTable Table(string[] columns, params object?[][] rows) => new(columns, rows);

    private static DatasetResult Result(string dataset, ComparisonResult comparison, string? hint = null) =>
        comparison.Passed
            ? new DatasetResult(dataset, DatasetOutcome.Passed, comparison.Message, comparison, null)
            : new DatasetResult(dataset, DatasetOutcome.Failed, comparison.Message, comparison, hint);

    private static DatasetResult Passed(string dataset) => Result(dataset, ComparisonResult.Pass);

    [Fact]
    public void Wrong_columns_are_one_problem_for_every_dataset_without_the_datasets_hints()
    {
        // Each dataset's check finds the same wrong columns, and each fills in its own hint about its data.
        ComparisonResult Columns() => ResultComparer.Compare(
            Table(["ProductName", "ListPrice", "DateAdded"], ["Gibson SG", 1208.42m, new DateTime(2016, 2, 5)]),
            Table(["ProductName", "ListPrice"], ["Gibson SG", 1208.42m]),
            CompareOptions.Default);

        var groups = DatasetGroup.Group(
        [
            Result("base", Columns()),
            Result("edge", Columns(), EdgeHint),
            Result("shifted", Columns(), ShiftedHint),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(["base", "edge", "shifted"], group.Datasets);
        Assert.True(group.AllDatasets);
        Assert.Equal("All datasets", group.Title);
        Assert.Equal(DatasetOutcome.Failed, group.Outcome);
        Assert.StartsWith("Expected columns: ProductName, ListPrice, DateAdded.", group.Message);
        Assert.Null(group.Hint);
    }

    [Fact]
    public void Datasets_with_the_same_message_and_rows_share_a_group_that_names_them()
    {
        var sorting = new ComparisonResult(false, "The rows are right, but they aren't in the right order (sorted by DateAdded).");

        var groups = DatasetGroup.Group([Result("base", sorting), Result("edge", sorting), Passed("shifted")]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["base", "edge"], groups[0].Datasets);
        Assert.Equal("Course data and edge-case data", groups[0].Title);
        Assert.False(groups[0].AllDatasets);
        Assert.Equal(DatasetOutcome.Failed, groups[0].Outcome);
        Assert.Equal("Shifted-ID data", groups[1].Title);
        Assert.Equal(DatasetOutcome.Passed, groups[1].Outcome);
    }

    [Fact]
    public void A_problem_on_one_dataset_still_names_that_dataset_and_its_hint()
    {
        var expected = Table(["ProductName", "ListPrice"], ["Gibson SG", 1208.42m]);
        var actual = Table(["ProductName", "ListPrice"], ["Gibson SG", 1208.42m], ["Edge Guitar 500", 500m]);
        var edge = ResultComparer.Compare(expected, actual, CompareOptions.Default);

        var groups = DatasetGroup.Group([Passed("base"), Result("edge", edge, EdgeHint), Passed("shifted")]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Course data and shifted-ID data", groups[0].Title);
        Assert.Equal(DatasetOutcome.Passed, groups[0].Outcome);

        Assert.Equal(["edge"], groups[1].Datasets);
        Assert.Equal("Edge-case data", groups[1].Title);
        Assert.Equal(EdgeHint, groups[1].Hint);
        Assert.Equal(["Edge Guitar 500", "500"], Assert.Single(groups[1].Comparison!.Unexpected!.Rows));
    }

    [Fact]
    public void The_same_message_with_different_rows_stays_apart()
    {
        // "1 row ... isn't in the expected result" on both, but they're different rows, which the student needs to see.
        var expected = Table(["Name"], ["Amy"]);
        var onBase = ResultComparer.Compare(expected, Table(["Name"], ["Amy"], ["Bo"]), CompareOptions.Default);
        var onEdge = ResultComparer.Compare(expected, Table(["Name"], ["Amy"], ["Cy"]), CompareOptions.Default);
        Assert.Equal(onBase.Message, onEdge.Message);

        var groups = DatasetGroup.Group([Result("base", onBase), Result("edge", onEdge)]);

        Assert.Equal(["Course data", "Edge-case data"], groups.Select(g => g.Title));
    }

    [Fact]
    public void A_problem_the_course_data_also_shows_drops_the_other_datasets_hints()
    {
        // Wrong order on the course data too, so the shifted data's hint about IDs isn't what explains it.
        var sorting = new ComparisonResult(false, "The rows are right, but they aren't in the right order (sorted by DateAdded).");

        var group = Assert.Single(DatasetGroup.Group([Result("base", sorting), Result("shifted", sorting, ShiftedHint)]));

        Assert.Equal("All datasets", group.Title);
        Assert.Null(group.Hint);
    }

    [Fact]
    public void A_problem_only_the_special_datasets_show_keeps_each_ones_hint()
    {
        var rowCount = new ComparisonResult(false, "Expected 3 rows, but your query returned 4 rows.");

        var groups = DatasetGroup.Group([Passed("base"), Result("edge", rowCount, EdgeHint), Result("shifted", rowCount, ShiftedHint)]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Edge-case data and shifted-ID data", groups[1].Title);
        Assert.Equal($"{EdgeHint} {ShiftedHint}", groups[1].Hint);
    }

    [Fact]
    public void The_same_sql_error_on_every_dataset_is_shown_once()
    {
        DatasetResult Error(string dataset) => new(dataset, DatasetOutcome.Error, "Invalid column name 'Price'.", null, null);

        var group = Assert.Single(DatasetGroup.Group([Error("base"), Error("edge"), Error("shifted")]));

        Assert.Equal(DatasetOutcome.Error, group.Outcome);
        Assert.Equal("All datasets", group.Title);
    }

    [Fact]
    public void Passing_everywhere_is_one_group()
    {
        var group = Assert.Single(DatasetGroup.Group([Passed("base"), Passed("edge"), Passed("shifted")]));

        Assert.Equal(DatasetOutcome.Passed, group.Outcome);
        Assert.Equal("All datasets", group.Title);
    }

    [Fact]
    public void A_single_dataset_is_named_rather_than_called_all()
    {
        var group = Assert.Single(DatasetGroup.Group([Passed("base")]));

        Assert.True(group.AllDatasets);
        Assert.Equal("Course data", group.Title);
    }

    [Fact]
    public void Dataset_names_read_as_a_list()
    {
        Assert.Equal("Course data", DatasetNames.List(["base"]));
        Assert.Equal("Edge-case data and shifted-ID data", DatasetNames.List(["edge", "shifted"]));
        Assert.Equal("Course data, edge-case data and shifted-ID data", DatasetNames.List(["base", "edge", "shifted"]));
        Assert.Equal("Holiday data and course data", DatasetNames.List(["holiday", "base"]));
    }

    [Fact]
    public void Only_differences_in_shape_are_structural()
    {
        var expected = Table(["A"], [1]);

        Assert.True(ResultComparer.Compare(expected, Table(["B"], [1]), CompareOptions.Default).Structural);
        Assert.True(ResultComparer.Compare(expected, Table(["A", "B"], [1, 2]), CompareOptions.Default with { Columns = ColumnMatch.ByPosition }).Structural);
        Assert.False(ResultComparer.Compare(expected, Table(["A"], [2]), CompareOptions.Default).Structural);
        Assert.False(ResultComparer.Compare(expected, Table(["A"]), CompareOptions.Default with { Columns = ColumnMatch.RowCount }).Structural);
    }
}
