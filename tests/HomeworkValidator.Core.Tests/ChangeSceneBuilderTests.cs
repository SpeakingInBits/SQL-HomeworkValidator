using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.Core.Tests;

public class ChangeSceneBuilderTests
{
    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    private static readonly TraceFilter CityIsReno = new("City = 'Reno'", ["City = 'Reno'"], [[true, false]], [true, false]);

    private static TraceChange Update() => new(
        ChangeKind.Update, "Vendors", "UPDATE Vendors SET Terms = 2 WHERE City = 'Reno'", "Terms = 2", "City = 'Reno'",
        ["Name", "City", "Terms"],
        [Row("Al", "Reno", "1"), Row("Bo", "Elko", "1")],
        CityIsReno,
        [true, false],
        [new ChangeRow(Row("Al", "Reno", "2"), 0, [2]), new ChangeRow(Row("Bo", "Elko", "1"), 1, [])],
        [], null);

    private static TraceChange Delete() => new(
        ChangeKind.Delete, "Vendors", "DELETE Vendors WHERE City = 'Reno'", null, "City = 'Reno'",
        ["Name", "City"],
        [Row("Al", "Reno"), Row("Bo", "Elko")],
        CityIsReno,
        [true, false],
        [new ChangeRow(Row("Bo", "Elko"), 1, [])],
        [], null);

    private static TraceChange Insert() => new(
        ChangeKind.Insert, "Categories", "INSERT INTO Categories (Name) VALUES ('Brass')", null, null,
        ["ID", "Name"],
        [Row("1", "Guitars")],
        null,
        [false],
        [new ChangeRow(Row("1", "Guitars"), 0, []), new ChangeRow(Row("2", "Brass"), -1, [])],
        ["ID"], null);

    [Fact]
    public void UPDATE_shows_the_table_before_and_after()
    {
        var scene = ChangeSceneBuilder.Build(Update());

        Assert.Equal("UPDATE Vendors", scene.Title);
        Assert.Equal("SET Terms = 2  ·  WHERE City = 'Reno'", scene.Subtitle);
        Assert.Contains(scene.Nodes, n => n is { Id: "before-title", Text: "Vendors before" });
        Assert.Contains(scene.Nodes, n => n is { Id: "after-title", Text: "Vendors after" });
    }

    [Fact]
    public void UPDATE_highlights_the_cells_it_changed()
    {
        var scene = ChangeSceneBuilder.Build(Update());

        Assert.Equal([2], scene.Nodes.Single(n => n.Id == "after-row-0").HighlightedCells);
        Assert.Empty(scene.Nodes.Single(n => n.Id == "after-row-1").HighlightedCells);
        Assert.Empty(scene.Nodes.Single(n => n.Id == "before-row-0").HighlightedCells);
    }

    [Fact]
    public void UPDATE_tests_each_row_then_says_what_changed()
    {
        var scene = ChangeSceneBuilder.Build(Update());

        Assert.Equal(
            ["Test each row of Vendors", "WHERE: test row 1 of 2", "Check: City = 'Reno'", "✓ Updated: Terms 1 → 2",
             "WHERE: test row 2 of 2", "Check: City = 'Reno'", "Row 2 of 2 stays as it is", "UPDATE complete: 1 row changed"],
            scene.Frames.Select(f => f.Phase));
        Assert.Equal(VizState.True, scene.Frames[2].States["card-0"]);
        Assert.Equal(VizState.False, scene.Frames[5].States["card-0"]);

        var updated = scene.Frames[3];
        Assert.Equal(VizState.New, updated.States["after-row-0"]);
        Assert.Contains("after-row-1", updated.Hidden);
        Assert.Equal("before-row-0", scene.Edges[Assert.Single(updated.Edges)].From);
    }

    [Fact]
    public void UPDATE_without_WHERE_says_every_row_is_affected()
    {
        var change = Update() with { Where = null, Filter = null, Hits = [true, true] };

        var scene = ChangeSceneBuilder.Build(change);

        Assert.Equal("SET Terms = 2  ·  No WHERE clause, so every row", scene.Subtitle);
        Assert.Equal("Every row of Vendors is affected", scene.Frames[0].Phase);
        Assert.DoesNotContain(scene.Nodes, n => n.Kind == VizNodeKind.Card);
    }

    [Fact]
    public void A_WHERE_that_depends_on_a_join_shows_what_happened_without_cards()
    {
        var change = Update() with { Filter = null };

        var scene = ChangeSceneBuilder.Build(change);

        Assert.DoesNotContain(scene.Nodes, n => n.Kind == VizNodeKind.Card);
        Assert.Contains(scene.Frames, f => f.Phase == "✓ Updated: Terms 1 → 2");
    }

    [Fact]
    public void An_UPDATE_that_sets_the_same_value_says_so()
    {
        var change = Update() with { After = [new ChangeRow(Row("Al", "Reno", "1"), 0, []), new ChangeRow(Row("Bo", "Elko", "1"), 1, [])] };

        var scene = ChangeSceneBuilder.Build(change);

        Assert.Contains(scene.Frames, f => f.Phase == "✓ Updated, but the new values are the same as the old ones");
    }

    [Fact]
    public void DELETE_strikes_out_rows_and_leaves_them_out_of_the_after_table()
    {
        var scene = ChangeSceneBuilder.Build(Delete());

        Assert.Equal("DELETE Vendors", scene.Title);
        var deleted = scene.Frames.Single(f => f.Phase == "✗ Row 1 of 2 is deleted");
        Assert.Equal(VizState.Cut, deleted.States["before-row-0"]);
        Assert.Equal("DELETE complete: 1 row deleted, 1 row left", scene.Frames[^1].Phase);
        Assert.Equal(["Bo"], scene.Nodes.Where(n => n.Id.StartsWith("after-row-", StringComparison.Ordinal)).Select(n => n.Cells![0]));
    }

    [Fact]
    public void INSERT_adds_rows_to_the_end_and_says_what_the_server_filled_in()
    {
        var scene = ChangeSceneBuilder.Build(Insert());

        Assert.Equal("INSERT Categories", scene.Title);
        Assert.Equal("Categories has 1 row, and this statement adds 1 row", scene.Frames[0].Phase);
        Assert.Contains("after-row-1", scene.Frames[0].Hidden);
        Assert.DoesNotContain("after-row-0", scene.Frames[0].Hidden);

        var added = scene.Frames[1];
        Assert.Equal("Add row 1 of 1. SQL Server fills in ID", added.Phase);
        Assert.Equal(VizState.New, added.States["after-row-1"]);
        Assert.Equal(("new-row-0", "after-row-1"), (scene.Edges[0].From, scene.Edges[0].To));
        Assert.Equal("INSERT complete: Categories now has 2 rows", scene.Frames[^1].Phase);
    }

    [Fact]
    public void A_statement_that_touches_no_rows_still_plays()
    {
        var change = Update() with
        {
            Hits = [false, false],
            After = [new ChangeRow(Row("Al", "Reno", "1"), 0, []), new ChangeRow(Row("Bo", "Elko", "1"), 1, [])],
        };

        Assert.Equal("UPDATE complete: 0 rows changed", ChangeSceneBuilder.Build(change).Frames[^1].Phase);
    }

    [Fact]
    public void An_empty_table_still_plays()
    {
        var change = Delete() with { Before = [], Hits = [], After = [], Filter = null };

        Assert.NotEmpty(ChangeSceneBuilder.Build(change).Frames);
    }
}
