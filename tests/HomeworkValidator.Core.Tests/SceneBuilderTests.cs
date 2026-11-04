using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.Core.Tests;

public class SceneBuilderTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[] values) =>
        values.Select(v => (IReadOnlyList<string>)[v]).ToList();

    private static QueryTrace Table(TraceFilter? filter = null, TraceSort? sort = null, IReadOnlyList<TraceCut>? cuts = null) => new(
        "Vendors", [], [], ["Name"], 1, Rows("Cy", "Al", "Bo"), [[0], [1], [2]], filter, sort, cuts ?? []);

    private static readonly TraceFilter NotBo = new("Name <> 'Bo'", ["Name <> 'Bo'"], [[true, true, false]], [true, true, false]);

    /// <summary>Vendors (Al, Bo) LEFT JOIN Invoices (x, y), where Al has both and Bo has neither.</summary>
    private static QueryTrace LeftJoin(TraceFilter? filter = null) => new(
        "Vendors",
        [new TraceSource("Vendors", null, ["Name"], Rows("Al", "Bo")), new TraceSource("Invoices", "i", ["Number"], Rows("x", "y"))],
        [new TraceJoin("LEFT JOIN", "Vendors.ID = i.VendorID", 0)],
        ["Name", "Number"], 2,
        [["Al", "x"], ["Al", "y"], ["Bo", "NULL"]],
        [[0, 0], [0, 1], [1, QueryTrace.NoMatch]],
        filter, null, [])
    {
        Comparisons = [[true, true], [false, false]],
    };

    private static IEnumerable<string> Cells(VizScene scene, string table) =>
        scene.Nodes.Where(n => n.Id.StartsWith($"{table}-row-", StringComparison.Ordinal)).Select(n => n.Cells![0]);

    [Fact]
    public void A_plain_query_is_a_scan_that_reads_each_row()
    {
        var scene = SceneBuilder.Build(Table());

        Assert.Equal("Vendors", scene.Title);
        Assert.Equal("A full table scan", scene.Subtitle);
        Assert.Equal(["Ready to scan", "Reading row 1 of 3", "Reading row 2 of 3", "Reading row 3 of 3", "Scan complete"], scene.Frames.Select(f => f.Phase));
        Assert.Equal(VizState.Scanning, scene.Frames[2].States["rows-row-1"]);
        Assert.Equal(VizState.Read, scene.Frames[2].States["rows-row-0"]);
        Assert.Equal(VizState.Pending, scene.Frames[2].States["rows-row-2"]);
    }

    [Fact]
    public void WHERE_tests_each_row_and_moves_the_ones_that_pass()
    {
        var scene = SceneBuilder.Build(Table(NotBo));

        Assert.Equal("Vendors → WHERE", scene.Title);
        Assert.Equal(["Cy", "Al"], Cells(scene, "matched"));
        Assert.Contains(scene.Nodes, n => n is { Id: "card-0", Kind: VizNodeKind.Card, Text: "Name <> 'Bo'" });
        Assert.Contains(scene.Frames, f => f.Phase == "✗ Filtered out by WHERE" && f.States["rows-row-2"] == VizState.Rejected);
        Assert.Contains(scene.Frames, f => f.States.GetValueOrDefault("card-0") == VizState.False);

        var last = scene.Frames[^1];
        Assert.Equal("Filter complete: 2 of 3 row(s) kept", last.Phase);
        Assert.Empty(last.Hidden);
        Assert.Equal(VizState.Matched, last.States["matched-row-1"]);
    }

    [Fact]
    public void Rows_appear_in_the_next_table_only_once_they_get_there()
    {
        var scene = SceneBuilder.Build(Table(NotBo));

        Assert.Contains("matched-row-0", scene.Frames[0].Hidden);
        var arrives = scene.Frames.ToList().FindIndex(f => !f.Hidden.Contains("matched-row-0"));
        Assert.Equal(VizState.New, scene.Frames[arrives].States["matched-row-0"]);
        Assert.Contains("matched-row-1", scene.Frames[arrives].Hidden);
        Assert.Equal("rows-row-0", scene.Edges[Assert.Single(scene.Frames[arrives].Edges)].From);
    }

    [Fact]
    public void ORDER_BY_places_rows_in_sorted_order()
    {
        var scene = SceneBuilder.Build(Table(sort: new TraceSort(["Name"], [1, 2, 0])));

        Assert.Equal("Vendors → ORDER BY", scene.Title);
        Assert.Equal(["Al", "Bo", "Cy"], Cells(scene, "sorted"));
        Assert.Equal("Sorted", scene.Frames[^1].Phase);

        // Connectors run from each row's place in the input to its place in the sorted table.
        Assert.Equal(("rows-row-1", "sorted-row-0"), (scene.Edges[0].From, scene.Edges[0].To));
        Assert.Equal(("rows-row-0", "sorted-row-2"), (scene.Edges[2].From, scene.Edges[2].To));
    }

    [Fact]
    public void WHERE_then_ORDER_BY_sorts_the_rows_that_passed()
    {
        var scene = SceneBuilder.Build(Table(NotBo, new TraceSort(["Name"], [1, 0])));

        Assert.Equal("Vendors → WHERE → ORDER BY", scene.Title);
        Assert.Equal(["Cy", "Al"], Cells(scene, "matched"));
        Assert.Equal(["Al", "Cy"], Cells(scene, "sorted"));
        // Row 1 of the input (Al) is the second row that passed, and sorts first.
        Assert.Contains(scene.Edges, e => e is { From: "matched-row-1", To: "sorted-row-0" });
    }

    [Fact]
    public void TOP_and_DISTINCT_mark_the_rows_they_remove()
    {
        var scene = SceneBuilder.Build(Table(sort: new TraceSort(["Name"], [1, 2, 0]), cuts: [new TraceCut("TOP 2 keeps the first 2 rows", [true, true, false])]));

        var last = scene.Frames[^1];
        Assert.Equal("TOP 2 keeps the first 2 rows", last.Phase);
        Assert.Equal(VizState.Kept, last.States["sorted-row-1"]);
        Assert.Equal(VizState.Cut, last.States["sorted-row-2"]);
    }

    [Fact]
    public void WHERE_that_matches_nothing_still_plays()
    {
        var scene = SceneBuilder.Build(Table(new TraceFilter("1 = 0", ["1 = 0"], [[false, false, false]], [false, false, false])));

        Assert.Equal("Filter complete: 0 of 3 row(s) kept", scene.Frames[^1].Phase);
    }

    [Fact]
    public void No_rows_at_all_still_plays()
    {
        var trace = new QueryTrace("Vendors", [], [], ["Name"], 1, [], [], null, null, []);

        var scene = SceneBuilder.Build(trace);

        Assert.NotEmpty(scene.Frames);
    }

    [Fact]
    public void A_join_forms_each_row_from_its_source_rows()
    {
        var scene = SceneBuilder.Build(LeftJoin());

        Assert.Equal("Vendors LEFT JOIN Invoices", scene.Title);
        Assert.Equal("ON Vendors.ID = i.VendorID", scene.Subtitle);
        Assert.True(scene.HasJoin);
        Assert.Equal("Ready: 3 joined row(s)", scene.Frames[0].Phase);

        var forming = scene.Frames[1];
        Assert.Equal("Forming joined row 1 of 3", forming.Phase);
        Assert.Equal(VizState.Active, forming.States["t0-row-0"]);
        Assert.Equal(VizState.Active, forming.States["t1-row-0"]);
        Assert.Contains("rows-row-0", forming.Hidden);
        var edge = scene.Edges[Assert.Single(forming.Edges)];
        Assert.Equal(("t0-row-0", "t1-row-0"), (edge.From, edge.To));
    }

    [Fact]
    public void An_outer_join_says_what_it_kept_and_marks_the_row()
    {
        var scene = SceneBuilder.Build(LeftJoin());

        var noMatch = scene.Frames.Single(f => f.Phase == "No match in Invoices: the outer join keeps this Vendors row");
        Assert.Equal(VizState.Outer, noMatch.States["t0-row-1"]);
        Assert.Empty(noMatch.Edges);

        var last = scene.Frames[^1];
        Assert.Equal("Join complete: 3 row(s), 1 with NULLs from the outer join", last.Phase);
        Assert.Equal(VizState.Outer, last.States["rows-row-2"]);
    }

    [Fact]
    public void The_in_depth_view_shows_failed_comparisons_too()
    {
        var scene = SceneBuilder.Build(LeftJoin());

        Assert.NotNull(scene.DetailFrames);
        var first = scene.DetailFrames.Single(f => f.Phase.StartsWith("Compare Vendors row 1", StringComparison.Ordinal));
        Assert.Equal("Compare Vendors row 1 with each Invoices row: ON is true for 2, false for 0", first.Phase);
        Assert.All(first.Edges, e => Assert.Equal(VizEdgeKind.Hit, scene.Edges[e].Kind));

        var second = scene.DetailFrames.Single(f => f.Phase.StartsWith("Compare Vendors row 2", StringComparison.Ordinal));
        Assert.Equal("Compare Vendors row 2 with each Invoices row: ON is false for all 2, so there's no match", second.Phase);
        Assert.All(second.Edges, e => Assert.Equal(VizEdgeKind.Miss, scene.Edges[e].Kind));
        Assert.Equal(VizState.Miss, second.States["t1-row-0"]);
    }

    [Fact]
    public void An_inner_join_says_it_drops_a_row_with_no_match()
    {
        var trace = LeftJoin() with
        {
            Joins = [new TraceJoin("JOIN", "Vendors.ID = i.VendorID", 0)],
            Rows = [["Al", "x"], ["Al", "y"]],
            Paths = [[0, 0], [0, 1]],
        };

        var scene = SceneBuilder.Build(trace);

        Assert.Equal("Vendors ⋈ Invoices", scene.Title);
        var dropped = scene.DetailFrames!.Single(f => f.Phase.EndsWith("so the JOIN drops this row", StringComparison.Ordinal));
        Assert.Equal(VizState.Rejected, dropped.States["t0-row-1"]);
        Assert.Equal(VizState.Rejected, scene.DetailFrames![^1].States["t0-row-1"]);
    }

    [Fact]
    public void WHERE_after_a_join_tests_the_joined_rows()
    {
        var scene = SceneBuilder.Build(LeftJoin(new TraceFilter("Number = 'y'", ["Number = 'y'"], [[false, true, false]], [false, true, false])));

        Assert.Equal("Vendors LEFT JOIN Invoices → WHERE", scene.Title);
        Assert.Contains(scene.Nodes, n => n is { Id: "rows-title", Text: "Joined rows (before WHERE)" });
        Assert.Equal(["Al"], Cells(scene, "matched"));
        Assert.Equal(VizState.Rejected, scene.Frames[^1].States["rows-row-2"]);
    }

    [Fact]
    public void Only_the_last_table_leaves_out_the_columns_added_for_explaining()
    {
        var trace = new QueryTrace("Vendors", [], [], ["Name", "State"], 1, [["Al", "CA"], ["Bo", "NY"]], [[0], [1]],
            new TraceFilter("State = 'CA'", ["State = 'CA'"], [[true, false]], [true, false]), null, []);

        var scene = SceneBuilder.Build(trace);

        Assert.Equal(["Name", "State"], scene.Nodes.Single(n => n.Id == "rows-header").Cells!);
        Assert.Equal(["Name"], scene.Nodes.Single(n => n.Id == "matched-header").Cells!);
    }

    [Fact]
    public void Wide_values_get_wider_columns_up_to_a_limit()
    {
        var trace = new QueryTrace("Vendors", [], [], ["ID", "Name"], 2, [["1", "A very long vendor name indeed"], ["2", new string('x', 500)]], [[0], [1]], null, null, []);

        var scene = SceneBuilder.Build(trace);

        var widths = scene.Nodes.Single(n => n.Id == "rows-row-0").CellWidths!;
        Assert.True(widths[0] < widths[1]);
        Assert.True(widths[1] <= 200);
    }
}
