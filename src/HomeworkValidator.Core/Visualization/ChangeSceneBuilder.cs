using static HomeworkValidator.Core.Visualization.SceneLayout;

namespace HomeworkValidator.Core.Visualization;

/// <summary>
/// Lays a <see cref="TraceChange"/> out as the table before and after the statement, and writes the frames that
/// take rows across. UPDATE and DELETE test each row of the table; INSERT adds rows to the end of it.
/// </summary>
public sealed class ChangeSceneBuilder
{
    private readonly TraceChange _change;
    private readonly List<VizNode> _nodes = [];
    private readonly List<VizEdge> _edges = [];
    private readonly List<VizFrame> _frames = [];
    private readonly Dictionary<string, VizState> _settled = [];
    private readonly HashSet<string> _hidden = [];

    private ChangeSceneBuilder(TraceChange change) => _change = change;

    public static VizScene Build(TraceChange change) => new ChangeSceneBuilder(change).Build();

    private string Verb => _change.Kind switch
    {
        ChangeKind.Insert => "INSERT",
        ChangeKind.Update => "UPDATE",
        _ => "DELETE",
    };

    private VizScene Build()
    {
        if (_change.Kind == ChangeKind.Insert)
            BuildInsert();
        else
            BuildUpdateOrDelete();

        return new VizScene($"{Verb} {_change.Table}", Subtitle(), _nodes, _edges, _frames,
            _nodes.Max(n => n.X + n.Width) + Pad, _nodes.Max(n => n.Y + n.Height) + Pad);
    }

    private string Subtitle()
    {
        if (_change.Kind == ChangeKind.Insert)
            return _change.Statement;

        var parts = new List<string>();
        if (_change.Set is { } set)
            parts.Add($"SET {set}");
        parts.Add(_change.Where is { } where ? $"WHERE {where}" : "No WHERE clause, so every row");
        return string.Join("  ·  ", parts);
    }

    private void Emit(IEnumerable<KeyValuePair<string, VizState>> now, IReadOnlyList<int> edges, string phase)
    {
        var states = new Dictionary<string, VizState>(_settled);
        foreach (var (id, state) in now)
            states[id] = state;
        _frames.Add(new VizFrame(states, _hidden.ToHashSet(), edges, phase));
    }

    private static KeyValuePair<string, VizState> State(string id, VizState state) => new(id, state);

    private static string Rows(int count) => count == 1 ? "1 row" : $"{count:N0} rows";

    // ----- INSERT -----

    private void BuildInsert()
    {
        var added = _change.After.Where(r => r.Before < 0).ToList();
        var y = Pad;

        var (newIds, width) = AddTable(_nodes, "new", "New rows", _change.Columns, added.Select(r => r.Cells).ToList(), Pad, y);
        var (afterIds, _) = AddTable(_nodes, "after", $"{_change.Table} after", _change.Columns, _change.After.Select(r => r.Cells).ToList(), Pad + width + TableGap, y);

        // New rows go on the end of the table.
        var firstNew = _change.After.Count - added.Count;
        for (var i = 0; i < added.Count; i++)
            _edges.Add(new VizEdge(newIds[i], afterIds[firstNew + i]));

        foreach (var id in newIds)
            _settled[id] = VizState.Pending;
        _hidden.UnionWith(afterIds.Skip(firstNew));
        Emit([], [], $"{_change.Table} has {Rows(firstNew)}, and this statement adds {Rows(added.Count)}");

        var filled = _change.Filled.Count > 0 ? $". SQL Server fills in {string.Join(", ", _change.Filled)}" : "";
        for (var i = 0; i < added.Count; i++)
        {
            var row = afterIds[firstNew + i];
            _hidden.Remove(row);
            Emit([State(newIds[i], VizState.Active), State(row, VizState.New)], [i], $"Add row {i + 1} of {added.Count}{filled}");
            _settled[newIds[i]] = VizState.Placed;
            _settled[row] = VizState.Matched;
        }

        Emit([], [], $"INSERT complete: {_change.Table} now has {Rows(_change.After.Count)}");
    }

    // ----- UPDATE and DELETE -----

    private void BuildUpdateOrDelete()
    {
        var y = Pad + (_change.Filter is not null ? CardArea : 0);
        var (beforeIds, width) = AddTable(_nodes, "before", $"{_change.Table} before", _change.Columns, _change.Before, Pad, y);
        var cardIds = _change.Filter is { } filter ? AddCards(_nodes, "card", filter.Conditions, Pad) : [];

        var (afterIds, _) = AddTable(_nodes, "after", $"{_change.Table} after", _change.Columns, _change.After.Select(r => r.Cells).ToList(),
            Pad + width + TableGap, y, i => _change.After[i].Changed);

        // Each row that's still there is connected to where it was.
        var afterOf = new Dictionary<int, int>();
        for (var i = 0; i < _change.After.Count; i++)
            afterOf[_change.After[i].Before] = i;
        var edgeOf = new Dictionary<int, int>();
        foreach (var (before, after) in afterOf)
        {
            edgeOf[before] = _edges.Count;
            _edges.Add(new VizEdge(beforeIds[before], afterIds[after]));
        }

        foreach (var id in beforeIds)
            _settled[id] = VizState.Pending;
        _hidden.UnionWith(afterIds);
        Emit([], [], _change.Where is null ? $"Every row of {_change.Table} is affected" : $"Test each row of {_change.Table}");

        var count = beforeIds.Count;
        for (var i = 0; i < count; i++)
        {
            var testing = State(beforeIds[i], VizState.Scanning);
            if (_change.Filter is { } test)
            {
                Emit([testing], [], $"WHERE: test row {i + 1} of {count}");
                var cards = new List<KeyValuePair<string, VizState>> { testing };
                for (var c = 0; c < test.Conditions.Count; c++)
                {
                    cards.Add(State(cardIds[c], test.ConditionResults[c][i] ? VizState.True : VizState.False));
                    Emit(cards, [], $"Check: {test.Conditions[c]}");
                }
            }

            var hit = _change.Hits[i];
            var isDeleted = hit && _change.Kind == ChangeKind.Delete;
            _settled[beforeIds[i]] = isDeleted ? VizState.Cut : hit ? VizState.Matched : VizState.Read;

            if (isDeleted)
            {
                Emit([], [], $"✗ Row {i + 1} of {count} is deleted");
                continue;
            }

            var after = afterIds[afterOf[i]];
            _hidden.Remove(after);
            Emit(hit ? [State(after, VizState.New)] : [], [edgeOf[i]], hit ? Updated(i) : $"Row {i + 1} of {count} stays as it is");
            if (hit)
                _settled[after] = VizState.Matched;
        }

        var hits = _change.Hits.Count(h => h);
        Emit([], [], _change.Kind == ChangeKind.Delete
            ? $"DELETE complete: {Rows(hits)} deleted, {Rows(_change.After.Count)} left"
            : $"UPDATE complete: {Rows(hits)} changed");
    }

    private string Updated(int row)
    {
        var after = _change.After.First(r => r.Before == row);
        if (after.Changed.Count == 0)
            return "✓ Updated, but the new values are the same as the old ones";
        return "✓ Updated: " + string.Join(", ", after.Changed.Select(c => $"{_change.Columns[c]} {_change.Before[row][c]} → {after.Cells[c]}"));
    }
}
