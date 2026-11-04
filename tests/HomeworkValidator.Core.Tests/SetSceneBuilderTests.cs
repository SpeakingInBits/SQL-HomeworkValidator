using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.Core.Tests;

public class SetSceneBuilderTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[] values) =>
        values.Select(v => (IReadOnlyList<string>)[v]).ToList();

    private static SetTable Table(string title, params string[] values) => new(title, ["Name"], Rows(values));

    private static SetRowFate Kept(int slot, params int[] related) => new(slot, related);
    private static SetRowFate Dropped(params int[] related) => new(-1, related);

    /// <summary>(Al, Bo) UNION (Bo, Cy): Bo is in both, so the second copy is left out.</summary>
    private static SetTrace Union(IReadOnlyList<string>? sortKeys = null, IReadOnlyList<int>? order = null) => new(
        "Query 1 UNION Query 2",
        [Table("Query 1", "Al", "Bo"), Table("Query 2", "Bo", "Cy"), Table("Result of UNION", "Al", "Bo", "Cy")],
        [new SetStep("UNION", 0, 1, 2, [Kept(0), Kept(1)], [Dropped(1), Kept(2)])],
        sortKeys ?? [], order ?? [],
        [new SetBranch(null, null), new SetBranch(null, null)],
        false);

    private static SetTrace Lookup(string keyword, SetTable result, params SetRowFate[] left) => new(
        $"Query 1 {keyword} Query 2",
        [Table("Query 1", "Al", "Bo"), Table("Query 2", "Bo", "Cy"), result],
        [new SetStep(keyword, 0, 1, 2, left, [])],
        [], [],
        [new SetBranch(null, null), new SetBranch(null, null)],
        false);

    private static VizScene Combine(SetTrace trace) => SetSceneBuilder.Build(trace)[^1];

    private static VizNode Node(VizScene scene, string id) => scene.Nodes.Single(n => n.Id == id);

    [Fact]
    public void UNION_reads_both_tables_and_leaves_out_the_second_copy()
    {
        var scene = Combine(Union());

        Assert.Equal("Combine: UNION", scene.Title);
        Assert.Equal("Query 1 UNION Query 2", scene.Subtitle);
        Assert.Equal(
            ["UNION: Query 1 has 2 rows, Query 2 has 2 rows",
             "✓ Query 1 row 1 of 2 is new, so it's added",
             "✓ Query 1 row 2 of 2 is new, so it's added",
             "✗ Query 2 row 1 of 2 is already in the result, so UNION leaves out this copy",
             "✓ Query 2 row 2 of 2 is new, so it's added",
             "UNION complete: 3 rows"],
            scene.Frames.Select(f => f.Phase));
    }

    [Fact]
    public void Rows_arrive_in_the_result_one_at_a_time()
    {
        var scene = Combine(Union());

        Assert.Contains("table-2-row-0", scene.Frames[0].Hidden);
        var first = scene.Frames[1];
        Assert.Equal(VizState.New, first.States["table-2-row-0"]);
        Assert.Contains("table-2-row-1", first.Hidden);
        var edge = scene.Edges[Assert.Single(first.Edges)];
        Assert.Equal(("table-0-row-0", "table-2-row-0"), (edge.From, edge.To));
        Assert.Empty(scene.Frames[^1].Hidden);
    }

    [Fact]
    public void A_duplicate_points_at_the_row_that_got_there_first()
    {
        var scene = Combine(Union());

        var duplicate = scene.Frames[3];
        Assert.Equal(VizState.Rejected, duplicate.States["table-1-row-0"]);
        Assert.Equal(VizState.Active, duplicate.States["table-2-row-1"]);
        var edge = scene.Edges[Assert.Single(duplicate.Edges)];
        Assert.Equal(("table-1-row-0", "table-2-row-1", VizEdgeKind.Miss), (edge.From, edge.To, edge.Kind));
    }

    [Fact]
    public void UNION_stacks_its_two_tables()
    {
        var scene = Combine(Union());

        var (first, second, result) = (Node(scene, "table-0-title"), Node(scene, "table-1-title"), Node(scene, "table-2-title"));
        Assert.Equal(first.X, second.X);
        Assert.True(second.Y > first.Y);
        Assert.True(result.X > first.X);
    }

    [Fact]
    public void UNION_ALL_keeps_every_row()
    {
        var trace = new SetTrace(
            "Query 1 UNION ALL Query 2",
            [Table("Query 1", "Al"), Table("Query 2", "Al"), Table("Result of UNION ALL", "Al", "Al")],
            [new SetStep("UNION ALL", 0, 1, 2, [Kept(0)], [Kept(1)])],
            [], [], [new SetBranch(null, null), new SetBranch(null, null)], false);

        var scene = Combine(trace);

        Assert.Contains(scene.Frames, f => f.Phase == "Query 2 row 1 of 1: UNION ALL keeps every row");
        Assert.Equal("UNION ALL complete: 2 rows", scene.Frames[^1].Phase);
    }

    [Fact]
    public void INTERSECT_looks_each_row_up_in_the_second_table()
    {
        var scene = Combine(Lookup("INTERSECT", Table("Result of INTERSECT", "Bo"), Dropped(), Kept(0, 0)));

        Assert.Equal(
            ["INTERSECT: Query 1 has 2 rows, Query 2 has 2 rows",
             "✗ Query 1 row 1 of 2 isn't in Query 2, so INTERSECT leaves it out",
             "✓ Query 1 row 2 of 2 is also in Query 2, so it stays",
             "INTERSECT complete: 1 row"],
            scene.Frames.Select(f => f.Phase));

        var found = scene.Frames[2];
        Assert.Equal(VizState.Active, found.States["table-1-row-0"]);
        Assert.Contains(found.Edges, e => scene.Edges[e] is { To: "table-1-row-0", Kind: VizEdgeKind.Hit });
    }

    [Fact]
    public void EXCEPT_removes_the_rows_it_finds_in_the_second_table()
    {
        var scene = Combine(Lookup("EXCEPT", Table("Result of EXCEPT", "Al"), Kept(0), Dropped(0)));

        Assert.Equal(
            ["EXCEPT: Query 1 has 2 rows, Query 2 has 2 rows",
             "✓ Query 1 row 1 of 2 isn't in Query 2, so it stays",
             "✗ Query 1 row 2 of 2 is also in Query 2, so EXCEPT removes it",
             "EXCEPT complete: 1 row"],
            scene.Frames.Select(f => f.Phase));
    }

    [Fact]
    public void INTERSECT_and_EXCEPT_put_their_tables_side_by_side()
    {
        var scene = Combine(Lookup("EXCEPT", Table("Result of EXCEPT", "Al"), Kept(0), Dropped(0)));

        var (first, second, result) = (Node(scene, "table-0-title"), Node(scene, "table-1-title"), Node(scene, "table-2-title"));
        Assert.Equal(first.Y, second.Y);
        Assert.True(second.X > first.X);
        Assert.True(result.X > second.X);
    }

    [Fact]
    public void A_chain_feeds_one_result_into_the_next_step()
    {
        var trace = new SetTrace(
            "Query 1 UNION Query 2 EXCEPT Query 3",
            [Table("Query 1", "Al"), Table("Query 2", "Bo"), Table("Query 3", "Bo"), Table("Result of UNION", "Al", "Bo"), Table("Result of EXCEPT", "Al")],
            [new SetStep("UNION", 0, 1, 3, [Kept(0)], [Kept(1)]), new SetStep("EXCEPT", 3, 2, 4, [Kept(0), Dropped(0)], [])],
            [], [], [new SetBranch(null, null), new SetBranch(null, null), new SetBranch(null, null)], false);

        var scene = Combine(trace);

        Assert.Equal("Combine: UNION, EXCEPT", scene.Title);
        Assert.Contains(scene.Frames, f => f.Phase == "✗ Result of UNION row 2 of 2 is also in Query 3, so EXCEPT removes it");
        Assert.Equal("EXCEPT complete: 1 row", scene.Frames[^1].Phase);
    }

    [Fact]
    public void ORDER_BY_places_the_combined_rows()
    {
        var scene = Combine(Union(["Name DESC"], [2, 1, 0]));

        Assert.Equal("Combine: UNION → ORDER BY", scene.Title);
        Assert.Equal("Query 1 UNION Query 2  ·  ORDER BY Name DESC", scene.Subtitle);
        Assert.Equal(["Cy", "Bo", "Al"], scene.Nodes.Where(n => n.Id.StartsWith("sorted-row-", StringComparison.Ordinal)).Select(n => n.Cells![0]));
        Assert.Equal("Sorted", scene.Frames[^1].Phase);

        var placing = scene.Frames.First(f => f.Phase == "Placing row 1 of 3");
        var edge = scene.Edges[Assert.Single(placing.Edges)];
        Assert.Equal(("table-2-row-2", "sorted-row-0"), (edge.From, edge.To));
    }

    [Fact]
    public void Rows_nothing_matched_still_end_up_in_the_result()
    {
        var trace = Union() with
        {
            Tables = [Table("Query 1", "Al"), Table("Query 2"), Table("Result of UNION", "Al", "Stray")],
            Steps = [new SetStep("UNION", 0, 1, 2, [Kept(0)], [])],
        };

        Assert.Empty(Combine(trace).Frames[^1].Hidden);
    }

    [Fact]
    public void Empty_tables_still_play()
    {
        var trace = Union() with
        {
            Tables = [Table("Query 1"), Table("Query 2"), Table("Result of UNION")],
            Steps = [new SetStep("UNION", 0, 1, 2, [], [])],
        };

        Assert.Equal("UNION complete: 0 rows", Combine(trace).Frames[^1].Phase);
    }

    [Fact]
    public void Each_query_with_an_animation_comes_first_with_its_number()
    {
        var branch = new QueryTrace("Vendors", [], [], ["Name"], 1, Rows("Al"), [[0]], null, null, []);
        var trace = Union() with { Branches = [new SetBranch(branch, null), new SetBranch(null, "It can't.")] };

        var scenes = SetSceneBuilder.Build(trace);

        Assert.Equal(["Query 1: Vendors", "Combine: UNION"], scenes.Select(s => s.Title));
        Assert.Contains("Query 2 has no animation of its own: It can't.", SetSceneBuilder.Note(trace));
    }

    [Fact]
    public void The_result_is_the_last_table_in_sorted_order()
    {
        Assert.Equal(["Al", "Bo", "Cy"], Union().Result.Select(r => r[0]));
        Assert.Equal(["Cy", "Bo", "Al"], Union(["Name DESC"], [2, 1, 0]).Result.Select(r => r[0]));
    }
}
