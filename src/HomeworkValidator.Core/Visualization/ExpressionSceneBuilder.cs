using static HomeworkValidator.Core.Visualization.SceneLayout;

namespace HomeworkValidator.Core.Visualization;

/// <summary>
/// Lays out a query with no FROM clause: its expressions on the left, and on the right the one row they make, filling in a value
/// at a time. The row is several nodes in the same place, one per step, as a node's cells don't change between frames.
/// </summary>
internal static class ExpressionSceneBuilder
{
    public static VizScene Build(QueryTrace trace)
    {
        var expressions = trace.Expressions!;
        var columns = trace.Columns;
        var values = trace.Rows[0];
        var count = columns.Count;

        var nodes = new List<VizNode>();
        var (expressionIds, width) = AddTable(nodes, "expressions", "Expressions", ["Column", "Expression"],
            Enumerable.Range(0, count).Select(i => (IReadOnlyList<string>)[columns[i], expressions[i]]).ToList(), Pad, Pad);

        var x = Pad + width + TableGap;
        var widths = CellWidths(columns, [values]);
        var (rowWidth, heading) = AddHeading(nodes, "row", "Result (one row)", columns, widths, x, Pad);

        // One version of the row for each new value, and then the finished row with nothing picked out.
        var stepIds = new List<string>();
        for (var step = 0; step <= count; step++)
        {
            var id = $"row-step-{step}";
            stepIds.Add(id);
            var cells = values.Select((value, c) => c <= step ? value : "").ToList();
            nodes.Add(new VizNode(id, VizNodeKind.Row, x, Pad + heading, rowWidth, RowHeight, Cells: cells, CellWidths: widths)
            {
                HighlightedCells = step < count ? [step] : [],
            });
        }

        var edges = Enumerable.Range(0, count).Select(i => new VizEdge(expressionIds[i], stepIds[i])).ToList();

        var frames = new List<VizFrame>
        {
            new(new Dictionary<string, VizState>(), stepIds.ToHashSet(), [], "There's no table to read, so SELECT works out one row from its expressions"),
        };

        for (var i = 0; i < count; i++)
        {
            var states = new Dictionary<string, VizState> { [expressionIds[i]] = VizState.Active, [stepIds[i]] = VizState.New };
            for (var done = 0; done < i; done++)
                states[expressionIds[done]] = VizState.Matched;

            // A plain value needs no working out, so there's nothing to say beyond the value.
            var phase = expressions[i] == values[i] ? $"{columns[i]}: {values[i]}" : $"{columns[i]}: {expressions[i]} = {values[i]}";
            frames.Add(new VizFrame(states, stepIds.Where(id => id != stepIds[i]).ToHashSet(), [i], phase));
        }

        var finished = expressionIds.ToDictionary(id => id, _ => VizState.Matched);
        finished[stepIds[count]] = VizState.Matched;
        var summary = string.Join(", ", columns.Select((column, c) => $"{column} = {values[c]}"));
        frames.Add(new VizFrame(finished, stepIds.Take(count).ToHashSet(), [], $"One row: {summary}"));

        return new VizScene("SELECT without FROM", "One row, worked out from the select list", nodes, edges, frames,
            nodes.Max(n => n.X + n.Width) + Pad, nodes.Max(n => n.Y + n.Height) + Pad);
    }
}
