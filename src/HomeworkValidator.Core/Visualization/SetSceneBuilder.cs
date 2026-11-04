using static HomeworkValidator.Core.Visualization.SceneLayout;

namespace HomeworkValidator.Core.Visualization;

/// <summary>
/// Turns a <see cref="SetTrace"/> into animations: one for each query being combined, using <see cref="SceneBuilder"/>, then one
/// that combines their rows. UNION stacks its two tables, as it brings rows from both. INTERSECT and EXCEPT put them side
/// by side, as they only look rows up in the second one.
/// </summary>
public sealed class SetSceneBuilder
{
    private readonly SetTrace _trace;
    private readonly List<VizNode> _nodes = [];
    private readonly List<VizEdge> _edges = [];
    private readonly List<VizFrame> _frames = [];
    private readonly Dictionary<string, VizState> _settled = [];
    private readonly HashSet<string> _hidden = [];

    /// <summary>Where each table went: its rows' nodes and its bounds.</summary>
    private readonly Dictionary<int, Placed> _placed = [];
    private List<string> _sortedIds = [];

    private SetSceneBuilder(SetTrace trace) => _trace = trace;

    /// <summary>The queries' own animations, where they have one, and the one that combines them.</summary>
    public static IReadOnlyList<VizScene> Build(SetTrace trace)
    {
        var scenes = new List<VizScene>();
        for (var i = 0; i < trace.Branches.Count; i++)
        {
            if (trace.Branches[i].Trace is { } branch)
            {
                var scene = SceneBuilder.Build(branch);
                scenes.Add(scene with { Title = $"Query {i + 1}: {scene.Title}" });
            }
        }

        scenes.Add(new SetSceneBuilder(trace).Build());
        return scenes;
    }

    /// <summary>What to tell the student about these animations.</summary>
    public static string Note(SetTrace trace)
    {
        var notes = new List<string> { "This runs each query on its own, then combines their rows. Choose a step from the list at the top." };
        for (var i = 0; i < trace.Branches.Count; i++)
        {
            if (trace.Branches[i].Problem is { } problem)
                notes.Add($"Query {i + 1} has no animation of its own: {problem}");
        }

        notes.AddRange(trace.Branches.Select(b => b.Trace?.Note).OfType<string>().Distinct());
        if (trace.Truncated)
            notes.Add($"Tables with more than {QueryTracer.MaxRows:N0} rows are cut off, which can change what's combined.");
        return string.Join(" ", notes);
    }

    private VizScene Build()
    {
        Layout();

        foreach (var step in _trace.Steps)
        {
            _hidden.UnionWith(_placed[step.Result].Ids);
        }

        _hidden.UnionWith(_sortedIds);

        foreach (var step in _trace.Steps)
            Write(step);
        if (_trace.SortKeys.Count > 0)
            WriteSort();

        var operators = string.Join(", ", _trace.Steps.Select(s => s.Operator).Distinct());
        var title = _trace.SortKeys.Count > 0 ? $"{operators} → ORDER BY" : operators;
        var subtitle = _trace.SortKeys.Count > 0 ? $"{_trace.Description}  ·  ORDER BY {string.Join(", ", _trace.SortKeys)}" : _trace.Description;
        return new VizScene($"Combine: {title}", subtitle, _nodes, _edges, _frames,
            _nodes.Max(n => n.X + n.Width) + Pad, _nodes.Max(n => n.Y + n.Height) + Pad);
    }

    // ----- Layout -----

    private void Layout()
    {
        var x = Pad;
        foreach (var step in _trace.Steps)
        {
            var (left, right) = (step.Left, step.Right);
            var (hasLeft, hasRight) = (_placed.ContainsKey(left), _placed.ContainsKey(right));

            if (!step.LooksUp)
            {
                // The one that isn't there yet goes under the one that is.
                if (!hasLeft && !hasRight)
                {
                    Place(left, x, Pad);
                    Place(right, x, _placed[left].Bottom + RowHeight);
                }
                else if (!hasRight)
                    Place(right, _placed[left].X, _placed[left].Bottom + RowHeight);
                else if (!hasLeft)
                    Place(left, _placed[right].X, _placed[right].Bottom + RowHeight);
            }
            else
            {
                if (!hasLeft)
                {
                    Place(left, x, Pad);
                    x = _placed[left].Right + TableGap;
                }

                if (!hasRight)
                    Place(right, x, Pad);
            }

            x = _placed.Values.Max(p => p.Right) + TableGap;
            Place(step.Result, x, Pad);
            x = _placed[step.Result].Right + TableGap;

            AddEdges(step);
        }

        if (_trace.SortKeys.Count > 0)
        {
            var last = _trace.Tables[_trace.Steps[^1].Result];
            (_sortedIds, _) = AddTable(_nodes, "sorted", $"Sorted by {string.Join(", ", _trace.SortKeys)}", last.Columns, _trace.Result, x, Pad);
        }
    }

    private void Place(int index, double x, double y)
    {
        var table = _trace.Tables[index];
        var (ids, width) = AddTable(_nodes, $"table-{index}", table.Title, table.Columns, table.Rows, x, y);
        _placed[index] = new Placed(ids, x, x + width, y + TitleHeight + HeaderHeight + table.Rows.Count * (RowHeight + RowGap));
    }

    /// <summary>Per step: the connectors for each row it reads, so a frame can light up just that row's.</summary>
    private readonly Dictionary<(int Table, int Row), List<int>> _rowEdges = [];

    private void AddEdges(SetStep step)
    {
        var result = _placed[step.Result].Ids;

        void Add(int table, IReadOnlyList<SetRowFate> rows)
        {
            var ids = _placed[table].Ids;
            for (var i = 0; i < rows.Count; i++)
            {
                var edges = new List<int>();
                var fate = rows[i];
                if (fate.Slot >= 0)
                    edges.Add(Edge(ids[i], result[fate.Slot], VizEdgeKind.Path));

                // INTERSECT and EXCEPT connect a row to its matches. UNION connects a duplicate to the row that got there first.
                foreach (var related in fate.Related)
                {
                    edges.Add(step.LooksUp
                        ? Edge(ids[i], _placed[step.Right].Ids[related], VizEdgeKind.Hit)
                        : Edge(ids[i], result[related], VizEdgeKind.Miss));
                }

                _rowEdges[(table, i)] = edges;
            }
        }

        Add(step.Left, step.LeftRows);
        Add(step.Right, step.RightRows);
    }

    private int Edge(string from, string to, VizEdgeKind kind)
    {
        _edges.Add(new VizEdge(from, to, kind));
        return _edges.Count - 1;
    }

    // ----- Frames -----

    private void Emit(IEnumerable<KeyValuePair<string, VizState>> now, IReadOnlyList<int> edges, string phase)
    {
        var states = new Dictionary<string, VizState>(_settled);
        foreach (var (id, state) in now)
            states[id] = state;
        _frames.Add(new VizFrame(states, _hidden.ToHashSet(), edges, phase));
    }

    private static KeyValuePair<string, VizState> State(string id, VizState state) => new(id, state);

    private static string Rows(int count) => count == 1 ? "1 row" : $"{count:N0} rows";

    private void Write(SetStep step)
    {
        var (left, right) = (_trace.Tables[step.Left], _trace.Tables[step.Right]);
        Emit([], [], $"{step.Operator}: {left.Title} has {Rows(left.Rows.Count)}, {right.Title} has {Rows(right.Rows.Count)}");

        WriteRows(step, step.Left, step.LeftRows, left.Title, right.Title);
        WriteRows(step, step.Right, step.RightRows, right.Title, right.Title);

        // Rows that couldn't be matched to one that was read still belong to the result.
        var result = _placed[step.Result].Ids;
        foreach (var id in result.Where(_hidden.Contains))
        {
            _hidden.Remove(id);
            _settled[id] = VizState.Matched;
        }

        Emit([], [], $"{step.Operator} complete: {Rows(result.Count)}");
    }

    private void WriteRows(SetStep step, int table, IReadOnlyList<SetRowFate> rows, string title, string otherTitle)
    {
        var ids = _placed[table].Ids;
        var result = _placed[step.Result].Ids;

        for (var i = 0; i < rows.Count; i++)
        {
            var fate = rows[i];
            var edges = _rowEdges[(table, i)];
            var phase = Phase(step, fate, $"{title} row {i + 1} of {rows.Count}", otherTitle);

            if (fate.Slot >= 0)
            {
                _hidden.Remove(result[fate.Slot]);
                _settled[ids[i]] = VizState.Matched;
                Emit(Related(step, fate).Append(State(result[fate.Slot], VizState.New)), edges, phase);
                _settled[result[fate.Slot]] = VizState.Matched;
            }
            else
            {
                _settled[ids[i]] = VizState.Rejected;
                Emit(Related(step, fate), edges, phase);
            }
        }
    }

    /// <summary>The rows a row is being compared with: matches in the second table, or the row it duplicates.</summary>
    private IEnumerable<KeyValuePair<string, VizState>> Related(SetStep step, SetRowFate fate)
    {
        var ids = step.LooksUp ? _placed[step.Right].Ids : _placed[step.Result].Ids;
        return fate.Related.Select(r => State(ids[r], VizState.Active));
    }

    private static string Phase(SetStep step, SetRowFate fate, string row, string otherTitle)
    {
        var kept = fate.Slot >= 0;
        var matched = fate.Related.Count > 0;
        return step.Operator switch
        {
            "UNION ALL" => $"{row}: UNION ALL keeps every row",
            "UNION" when kept => $"✓ {row} is new, so it's added",
            "UNION" => $"✗ {row} is already in the result, so UNION leaves out this copy",
            "INTERSECT" when kept => $"✓ {row} is also in {otherTitle}, so it stays",
            "INTERSECT" when matched => $"✗ {row} is already in the result, so INTERSECT leaves out this copy",
            "INTERSECT" => $"✗ {row} isn't in {otherTitle}, so INTERSECT leaves it out",
            _ when kept => $"✓ {row} isn't in {otherTitle}, so it stays",
            _ when matched => $"✗ {row} is also in {otherTitle}, so EXCEPT removes it",
            _ => $"✗ {row} is already in the result, so EXCEPT leaves out this copy",
        };
    }

    private void WriteSort()
    {
        var from = _placed[_trace.Steps[^1].Result].Ids;
        for (var i = 0; i < _sortedIds.Count; i++)
        {
            var source = from[_trace.SortOrder[i]];
            _hidden.Remove(_sortedIds[i]);
            var edge = Edge(source, _sortedIds[i], VizEdgeKind.Path);
            Emit([State(source, VizState.Active), State(_sortedIds[i], VizState.New)], [edge], $"Placing row {i + 1} of {_sortedIds.Count}");
            _settled[source] = VizState.Placed;
            _settled[_sortedIds[i]] = VizState.Placed;
        }

        Emit([], [], "Sorted");
    }

    private sealed record Placed(List<string> Ids, double X, double Right, double Bottom);
}
