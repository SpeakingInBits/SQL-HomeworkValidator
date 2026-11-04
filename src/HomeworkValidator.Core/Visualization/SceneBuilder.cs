using static HomeworkValidator.Core.Visualization.SceneLayout;

namespace HomeworkValidator.Core.Visualization;

/// <summary>
/// Lays a <see cref="QueryTrace"/> out as tables from left to right, one per step of the query, and writes the animation
/// frames that move rows between them. Pure layout: no SQL.
/// </summary>
public sealed class SceneBuilder
{
    private const double GroupHeight = 26;

    private readonly QueryTrace _trace;
    private readonly List<VizNode> _nodes = [];
    private readonly List<VizEdge> _edges = [];

    private readonly List<List<string>> _sourceIds = [];
    private List<string> _cardIds = [];

    /// <summary>The joined rows, or the rows of the only table: one node per row of the trace.</summary>
    private List<string> _rowIds = [];

    /// <summary>Rows that passed WHERE.</summary>
    private readonly Passed _matched = new();

    /// <summary>Per row of the trace that reached the groups: its node in its group, and the connector there.</summary>
    private readonly Dictionary<int, string> _bucketIds = [];
    private readonly Dictionary<int, int> _bucketEdges = [];

    /// <summary>Per group: its heading and its rows.</summary>
    private readonly List<string> _groupIds = [];
    private readonly List<List<int>> _groupRows = [];

    /// <summary>What the groups produce, and per one of those rows the connectors from what produced it.</summary>
    private List<string> _summaryIds = [];
    private readonly List<List<int>> _summaryEdges = [];

    /// <summary>Summary rows that passed HAVING.</summary>
    private List<string> _havingCardIds = [];
    private readonly Passed _having = new();

    /// <summary>Rows in ORDER BY order.</summary>
    private List<string> _sortedIds = [];
    private readonly List<int> _sortedEdges = [];

    /// <summary>[joined row]: the connectors between the source rows that form it.</summary>
    private readonly List<List<int>> _pathEdges = [];

    /// <summary>[left row][right row]: the connector for the comparison of the pair.</summary>
    private readonly List<List<int>> _comparisonEdges = [];

    private SceneBuilder(QueryTrace trace) => _trace = trace;

    private bool HasJoin => _trace.Sources.Count > 0;
    private int SourceCount => _trace.Sources.Count;
    private int RowCount => _trace.Rows.Count;

    public static VizScene Build(QueryTrace trace) =>
        trace.Expressions is not null ? ExpressionSceneBuilder.Build(trace) : new SceneBuilder(trace).Build();

    private VizScene Build()
    {
        Layout();

        var frames = new FrameWriter(this, inDepth: false).Write();
        var detail = _trace.Comparisons is not null ? new FrameWriter(this, inDepth: true).Write() : null;

        return new VizScene(Title(), Subtitle(), _nodes, _edges, frames,
            _nodes.Max(n => n.X + n.Width) + Pad, _nodes.Max(n => n.Y + n.Height) + Pad,
            HasJoin, detail, _trace.ComparisonsUnavailable);
    }

    // ----- Layout -----

    private void Layout()
    {
        var hasCards = _trace.Filter is not null || _trace.Grouping?.Having is not null;
        var y = Pad + (hasCards ? CardArea : 0);
        var x = Pad;

        for (var k = 0; k < SourceCount; k++)
        {
            var source = _trace.Sources[k];
            var (ids, width) = AddTable(_nodes, $"t{k}", source.Label, source.Columns, source.Rows, x, y);
            _sourceIds.Add(ids);
            x += width + TableGap;
        }

        var filter = _trace.Filter;
        var sort = _trace.Sort;
        var grouping = _trace.Grouping;
        var everyColumn = _trace.Columns.Count;

        var inputTitle = HasJoin ? (filter is null ? "Joined rows" : "Joined rows (before WHERE)") : _trace.Title;
        var inputIsLast = filter is null && sort is null && grouping is null;
        var (rowIds, inputWidth) = AddTable(_nodes, "rows", inputTitle, Columns(inputIsLast ? _trace.ResultColumns : everyColumn),
            Rows(Enumerable.Range(0, RowCount), inputIsLast ? _trace.ResultColumns : everyColumn), x, y);
        _rowIds = rowIds;
        if (filter is not null)
            _cardIds = AddCards(_nodes, "card", filter.Conditions, x);
        x += inputWidth + TableGap;

        if (filter is not null)
        {
            var passing = Enumerable.Range(0, RowCount).Where(i => filter.Passes[i]).ToList();
            var isLast = sort is null && grouping is null;
            var columns = isLast ? _trace.ResultColumns : everyColumn;
            var (ids, width) = AddTable(_nodes, "matched", isLast ? "Result (WHERE)" : "Matched (WHERE)", Columns(columns), Rows(passing, columns), x, y);
            Connect(_matched, ids, passing, i => _rowIds[i]);
            x += width + TableGap;
        }

        if (grouping is not null)
            x = LayoutGroups(grouping, x, y);

        if (sort is not null)
        {
            var (columns, rows) = grouping is not null
                ? (grouping.Columns, sort.Order.Select(i => grouping.Rows[i]).ToList())
                : (Columns(_trace.ResultColumns), Rows(sort.Order, _trace.ResultColumns));
            var (ids, _) = AddTable(_nodes, "sorted", $"Sorted by {string.Join(", ", sort.Keys)}", columns, rows, x, y);
            _sortedIds = ids;
            for (var i = 0; i < sort.Order.Count; i++)
            {
                _sortedEdges.Add(_edges.Count);
                _edges.Add(new VizEdge(SortSource(sort.Order[i]), ids[i]));
            }
        }

        if (HasJoin)
            AddJoinEdges();
    }

    /// <summary>Records the table of rows that passed a test, and connects each to the node it came from.</summary>
    private void Connect(Passed passed, List<string> ids, List<int> rows, Func<int, string> from)
    {
        passed.Ids = ids;
        for (var slot = 0; slot < rows.Count; slot++)
        {
            passed.Slots[rows[slot]] = slot;
            passed.Edges[rows[slot]] = _edges.Count;
            _edges.Add(new VizEdge(from(rows[slot]), ids[slot]));
        }
    }

    private double LayoutGroups(TraceGrouping grouping, double x, double y)
    {
        var noun = grouping.Collapses ? "Groups" : "Partitions";
        var inGroups = Enumerable.Range(0, RowCount).Where(i => grouping.GroupOfRow[i] >= 0).ToList();

        var title = grouping.Clause is { } clause ? $"{noun} ({clause})" : noun;
        var widths = CellWidths(_trace.Columns, inGroups.Select(i => _trace.Rows[i]));
        var (width, heading) = AddHeading(_nodes, "groups", title, _trace.Columns, widths, x, y);

        var rowY = y + heading;
        for (var group = 0; group < grouping.Labels.Count; group++)
        {
            var members = inGroups.Where(i => grouping.GroupOfRow[i] == group).ToList();
            _groupRows.Add(members);
            var label = grouping.Labels[group];
            _groupIds.Add($"group-{group}");
            _nodes.Add(new VizNode(_groupIds[group], VizNodeKind.Group, x, rowY, width, GroupHeight, Text: $"{label} ({Rows(members.Count)})"));
            rowY += GroupHeight + RowGap;

            foreach (var row in members)
            {
                var id = $"group-row-{row}";
                _bucketIds[row] = id;
                _nodes.Add(new VizNode(id, VizNodeKind.Row, x, rowY, width, RowHeight, Cells: _trace.Rows[row], CellWidths: widths));
                _bucketEdges[row] = _edges.Count;
                _edges.Add(new VizEdge(RowNode(row), id));
                rowY += RowHeight + RowGap;
            }

            rowY += RowGap * 2;
        }

        x += width + TableGap;

        // HAVING and ORDER BY carry on from the rows the groups produce.
        var title2 = grouping.Collapses ? "One row per group" : "Result";
        var (summaryIds, summaryWidth) = AddTable(_nodes, "summary", title2, grouping.Columns, grouping.Rows, x, y);
        _summaryIds = summaryIds;
        for (var i = 0; i < grouping.Rows.Count; i++)
        {
            var source = grouping.Sources[i];
            var edges = new List<int>();
            if (source >= 0)
            {
                edges.Add(_edges.Count);
                _edges.Add(new VizEdge(grouping.Collapses ? _groupIds[source] : _bucketIds[source], summaryIds[i]));
            }

            _summaryEdges.Add(edges);
        }

        if (grouping.Having is { } having)
        {
            _havingCardIds = AddCards(_nodes, "having-card", having.Conditions, x);
            x += summaryWidth + TableGap;
            var passing = Enumerable.Range(0, grouping.Rows.Count).Where(i => having.Passes[i]).ToList();
            var (ids, width3) = AddTable(_nodes, "having", _trace.Sort is null ? "Result (HAVING)" : "Matched (HAVING)", grouping.Columns,
                passing.Select(i => grouping.Rows[i]).ToList(), x, y);
            Connect(_having, ids, passing, i => _summaryIds[i]);
            return x + width3 + TableGap;
        }

        return x + summaryWidth + TableGap;
    }

    private static string Rows(int count) => count == 1 ? "1 row" : $"{count} rows";

    private IReadOnlyList<string> Columns(int count) => _trace.Columns.Take(count).ToList();

    private List<IReadOnlyList<string>> Rows(IEnumerable<int> rows, int columns) =>
        rows.Select(i => (IReadOnlyList<string>)_trace.Rows[i].Take(columns).ToList()).ToList();

    /// <summary>A row's node in the last table before grouping.</summary>
    private string RowNode(int row) => _trace.Filter is null ? _rowIds[row] : _matched.Ids[_matched.Slots[row]];

    /// <summary>The node a row is sorted from: the table just before the sorted one.</summary>
    private string SortSource(int row)
    {
        if (_trace.Grouping is not { } grouping)
            return RowNode(row);
        return grouping.Having is null ? _summaryIds[row] : _having.Ids[_having.Slots[row]];
    }

    /// <summary>The table the query's result ends up in.</summary>
    private List<string> LastTable()
    {
        if (_trace.Sort is not null)
            return _sortedIds;
        if (_trace.Grouping is { } grouping)
            return grouping.Having is null ? _summaryIds : _having.Ids;
        return _trace.Filter is not null ? _matched.Ids : _rowIds;
    }

    private void AddJoinEdges()
    {
        // One connector per pair of tables in each joined row, so exactly its source rows light up.
        // A table an outer join found no match in has no row to connect.
        foreach (var path in _trace.Paths)
        {
            var segments = new List<int>();
            for (var k = 0; k < _trace.Joins.Count; k++)
            {
                var left = _trace.Joins[k].LeftSource;
                if (path[left] == QueryTrace.NoMatch || path[k + 1] == QueryTrace.NoMatch)
                    continue;
                segments.Add(_edges.Count);
                _edges.Add(new VizEdge(_sourceIds[left][path[left]], _sourceIds[k + 1][path[k + 1]]));
            }

            _pathEdges.Add(segments);
        }

        if (_trace.Comparisons is not { } comparisons)
            return;
        for (var l = 0; l < comparisons.Length; l++)
        {
            var row = new List<int>();
            for (var r = 0; r < comparisons[l].Length; r++)
            {
                row.Add(_edges.Count);
                _edges.Add(new VizEdge(_sourceIds[0][l], _sourceIds[1][r], comparisons[l][r] ? VizEdgeKind.Hit : VizEdgeKind.Miss));
            }

            _comparisonEdges.Add(row);
        }
    }

    // ----- Titles -----

    private string Title()
    {
        var parts = new List<string> { FromTitle() };
        if (_trace.Filter is not null)
            parts.Add("WHERE");
        if (_trace.Grouping is { } grouping)
        {
            parts.Add(grouping.Collapses ? (grouping.Clause is null ? "Aggregate" : "GROUP BY") : "OVER");
            if (grouping.Having is not null)
                parts.Add("HAVING");
        }

        if (_trace.Sort is not null)
            parts.Add("ORDER BY");
        return string.Join(" → ", parts);
    }

    private string FromTitle()
    {
        if (!HasJoin)
            return _trace.Title;

        // Reads like the FROM clause: "a ⋈ b" for inner joins, the keyword otherwise.
        var title = _trace.Sources[0].Table;
        for (var k = 0; k < _trace.Joins.Count; k++)
        {
            var (type, table) = (_trace.Joins[k].Type, _trace.Sources[k + 1].Table);
            title += type switch
            {
                "JOIN" => $" ⋈ {table}",
                "," => $", {table}",
                _ => $" {type} {table}",
            };
        }

        return title;
    }

    private string Subtitle()
    {
        var parts = _trace.Joins.Where(j => j.On is not null).Select(j => $"ON {j.On}").ToList();
        if (_trace.Filter is { } filter)
            parts.Add($"WHERE {filter.Text}");
        if (_trace.Grouping?.Clause is { } clause)
            parts.Add(clause);
        if (_trace.Grouping?.Having is { } having)
            parts.Add($"HAVING {having.Text}");
        if (_trace.Sort is { } sort)
            parts.Add($"ORDER BY {string.Join(", ", sort.Keys)}");
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "A full table scan";
    }

    /// <summary>A table of rows that passed a test: its nodes, and per row tested, its position there and the connector to it.</summary>
    private sealed class Passed
    {
        public List<string> Ids { get; set; } = [];
        public Dictionary<int, int> Slots { get; } = [];
        public Dictionary<int, int> Edges { get; } = [];
    }

    // ----- Frames -----

    /// <summary>
    /// Writes the frames for one playback mode. <see cref="_settled"/> holds how every finished step looks, so
    /// each frame is that plus whatever is happening right now.
    /// </summary>
    private sealed class FrameWriter(SceneBuilder scene, bool inDepth)
    {
        private readonly QueryTrace _trace = scene._trace;
        private readonly List<VizFrame> _frames = [];
        private readonly Dictionary<string, VizState> _settled = [];
        private readonly HashSet<string> _hidden = [];

        public List<VizFrame> Write()
        {
            _hidden.UnionWith(scene._matched.Ids);
            _hidden.UnionWith(scene._groupIds);
            _hidden.UnionWith(scene._bucketIds.Values);
            _hidden.UnionWith(scene._summaryIds);
            _hidden.UnionWith(scene._having.Ids);
            _hidden.UnionWith(scene._sortedIds);

            if (scene.HasJoin)
                WriteJoin();
            else if (_trace.Filter is null && _trace.Sort is null && _trace.Grouping is null)
                WriteScan();
            else if (_trace.Filter is null)
                Emit([], [], $"Rows of {_trace.Title}, in the order they're stored");

            if (_trace.Filter is { } filter)
                WriteWhere(filter);
            if (_trace.Grouping is { } grouping)
                WriteGroups(grouping);
            if (_trace.Sort is { } sort)
                WriteSort(sort);
            WriteCuts();
            return _frames;
        }

        private void Emit(IEnumerable<KeyValuePair<string, VizState>> now, IReadOnlyList<int> edges, string phase)
        {
            var states = new Dictionary<string, VizState>(_settled);
            foreach (var (id, state) in now)
                states[id] = state;
            _frames.Add(new VizFrame(states, _hidden.ToHashSet(), edges, phase));
        }

        private void Emit(Dictionary<string, VizState> now, string phase) => Emit(now, [], phase);

        private static KeyValuePair<string, VizState> State(string id, VizState state) => new(id, state);

        // ----- A plain table scan -----

        private void WriteScan()
        {
            var ids = scene._rowIds;
            foreach (var id in ids)
                _settled[id] = VizState.Pending;
            Emit([], [], "Ready to scan");

            for (var i = 0; i < ids.Count; i++)
            {
                Emit([State(ids[i], VizState.Scanning)], [], $"Reading row {i + 1} of {ids.Count}");
                _settled[ids[i]] = VizState.Read;
            }

            Emit([], [], "Scan complete");
        }

        // ----- Joins -----

        private bool HasRow(int[] path, int k) => path[k] != QueryTrace.NoMatch;
        private bool IsOuter(int[] path) => path.Contains(QueryTrace.NoMatch);

        /// <summary>A name for a table in the phase text. Aliases are only needed to tell apart the two sides of a self-join.</summary>
        private string Name(int k)
        {
            var source = _trace.Sources[k];
            return source.Alias is not null && _trace.Sources.Count(s => s.Table == source.Table) > 1 ? source.Alias : source.Table;
        }

        private void WriteJoin()
        {
            var sources = scene._sourceIds;
            var rows = scene._rowIds;

            // While joining, every source row is dim unless it's been used, and joined rows appear one at a time.
            foreach (var id in sources.SelectMany(ids => ids))
                _settled[id] = VizState.Dim;
            _hidden.UnionWith(rows);

            Emit([], [], inDepth
                ? $"Ready: test each {Name(0)} row against every {Name(1)} row ({sources[0].Count} × {sources[1].Count} comparisons)"
                : $"Ready: {rows.Count} joined row(s)");

            if (inDepth)
            {
                // A nested loop: a left row's comparisons, then the joined rows it forms. Rows with
                // no left partner (RIGHT and FULL joins) come last.
                var next = 0;
                for (var left = 0; left < sources[0].Count; left++)
                {
                    Compare(left);
                    while (next < rows.Count && _trace.Paths[next][0] == left)
                        EmitPath(next++);
                }

                while (next < rows.Count)
                    EmitPath(next++);
            }
            else
            {
                for (var i = 0; i < rows.Count; i++)
                    EmitPath(i);
            }

            // Dim was only for the join; rows it never used go back to normal.
            foreach (var id in sources.SelectMany(ids => ids).Where(id => _settled[id] == VizState.Dim))
                _settled.Remove(id);

            var outer = _trace.Paths.Count(IsOuter);
            Emit([], [], outer > 0
                ? $"Join complete: {rows.Count} row(s), {outer} with NULLs from the outer join"
                : $"Join complete: {rows.Count} row(s)");
        }

        private void EmitPath(int row)
        {
            var path = _trace.Paths[row];
            var outer = IsOuter(path);
            var tables = Enumerable.Range(0, path.Length).ToList();

            var active = tables.Where(k => HasRow(path, k))
                .Select(k => State(scene._sourceIds[k][path[k]], outer ? VizState.Outer : VizState.Active)).ToList();

            var (forming, adding) = ($"Forming joined row {row + 1} of {scene._rowIds.Count}", $"Add joined row {row + 1} to the result");
            if (outer)
            {
                var kept = string.Join(" + ", tables.Where(k => HasRow(path, k)).Select(Name));
                var missing = string.Join(", ", tables.Where(k => !HasRow(path, k)).Select(Name));
                forming = $"No match in {missing}: the outer join keeps this {kept} row";
                adding = $"Add joined row {row + 1} with NULLs for {missing}";
            }

            // Forming: the connectors run across the tables, and the joined row isn't there yet.
            Emit(active, scene._pathEdges[row], forming);

            // Adding: the finished row drops into the joined table.
            _hidden.Remove(scene._rowIds[row]);
            Emit(active.Append(State(scene._rowIds[row], VizState.New)), scene._pathEdges[row], adding);

            _settled[scene._rowIds[row]] = outer ? VizState.Outer : VizState.Matched;
            foreach (var k in tables.Where(k => HasRow(path, k)))
                _settled[scene._sourceIds[k][path[k]]] = VizState.Matched;
        }

        /// <summary>In-depth: tests a left row against every right row in one step.</summary>
        private void Compare(int left)
        {
            var results = _trace.Comparisons![left];
            var now = new Dictionary<string, VizState> { [scene._sourceIds[0][left]] = VizState.Active };
            for (var r = 0; r < results.Length; r++)
                now[scene._sourceIds[1][r]] = results[r] ? VizState.Active : VizState.Miss;

            var hits = results.Count(hit => hit);
            var phase = $"Compare {Name(0)} row {left + 1} with each {Name(1)} row: ON is ";
            if (hits > 0)
                phase += $"true for {hits}, false for {results.Length - hits}";
            else if (_trace.Paths.Any(p => p[0] == left))
                phase += $"false for all {results.Length}, so there's no match";
            else
            {
                phase += $"false for all {results.Length}, so the {_trace.Joins[0].Type} drops this row";
                _settled[scene._sourceIds[0][left]] = VizState.Rejected;
                now[scene._sourceIds[0][left]] = VizState.Rejected;
            }

            Emit(now, scene._comparisonEdges[left], phase);
        }

        // ----- WHERE and HAVING -----

        private void WriteWhere(TraceFilter filter)
        {
            if (!scene.HasJoin)
            {
                foreach (var id in scene._rowIds)
                    _settled[id] = VizState.Pending;
                Emit([], [], "Start scan");
            }

            WriteTest("WHERE", filter, scene._rowIds, scene._cardIds, scene._matched,
                i => scene.HasJoin && IsOuter(_trace.Paths[i]) ? VizState.Outer : VizState.Matched);
        }

        /// <summary>Tests each row against a clause's conditions, moving the ones that pass to the next table.</summary>
        private void WriteTest(string clause, TraceFilter filter, List<string> ids, List<string> cardIds, Passed passed, Func<int, VizState> passState)
        {
            var count = ids.Count;
            for (var i = 0; i < count; i++)
            {
                var testing = State(ids[i], VizState.Scanning);
                Emit([testing], [], $"{clause}: test row {i + 1} of {count}");

                var cards = new List<KeyValuePair<string, VizState>> { testing };
                for (var c = 0; c < filter.Conditions.Count; c++)
                {
                    cards.Add(State(cardIds[c], filter.ConditionResults[c][i] ? VizState.True : VizState.False));
                    Emit(cards, [], $"Check: {filter.Conditions[c]}");
                }

                if (filter.Passes[i])
                {
                    var kept = passed.Ids[passed.Slots[i]];
                    _hidden.Remove(kept);
                    _settled[ids[i]] = passState(i);
                    Emit([State(kept, VizState.New)], [passed.Edges[i]], $"✓ Kept by {clause}");
                    _settled[kept] = VizState.Matched;
                }
                else
                {
                    _settled[ids[i]] = VizState.Rejected;
                    Emit([], [], $"✗ Filtered out by {clause}");
                }
            }

            Emit([], [], clause == "WHERE"
                ? $"Filter complete: {passed.Ids.Count} of {count} row(s) kept"
                : $"{clause} complete: {passed.Ids.Count} of {count} row(s) kept");
        }

        // ----- GROUP BY and window functions -----

        private void WriteGroups(TraceGrouping grouping)
        {
            var noun = grouping.Collapses ? "group" : "partition";
            var members = scene._bucketIds.Keys.Order().ToList();

            for (var i = 0; i < members.Count; i++)
            {
                var row = members[i];
                var group = grouping.GroupOfRow[row];
                var (from, to) = (scene.RowNode(row), scene._bucketIds[row]);
                _hidden.Remove(scene._groupIds[group]);
                _hidden.Remove(to);
                Emit([State(from, VizState.Active), State(to, VizState.New)], [scene._bucketEdges[row]],
                    $"Row {i + 1} of {members.Count} goes into the {noun} for {grouping.Labels[group]}");
                _settled[from] = VizState.Placed;
            }

            // An aggregate over no rows at all still gives a row, so its group has to appear too.
            foreach (var id in scene._groupIds)
                _hidden.Remove(id);
            Emit([], [], $"{Count(scene._groupIds.Count, noun)} of rows");

            for (var group = 0; group < scene._groupIds.Count; group++)
            {
                var rows = scene._groupRows[group];
                var results = Enumerable.Range(0, grouping.Rows.Count)
                    .Where(i => grouping.Collapses ? grouping.Sources[i] == group : rows.Contains(grouping.Sources[i]))
                    .ToList();
                if (results.Count == 0)
                    continue;

                var now = rows.Select(r => State(scene._bucketIds[r], VizState.Active)).Append(State(scene._groupIds[group], VizState.Active));
                foreach (var result in results)
                    _hidden.Remove(scene._summaryIds[result]);
                Emit(now.Concat(results.Select(r => State(scene._summaryIds[r], VizState.New))),
                    results.SelectMany(r => scene._summaryEdges[r]).ToList(),
                    SummaryPhase(grouping, group, rows.Count, results[0]));

                foreach (var row in rows)
                    _settled[scene._bucketIds[row]] = VizState.Matched;
                _settled[scene._groupIds[group]] = VizState.Matched;
                foreach (var result in results)
                    _settled[scene._summaryIds[result]] = VizState.Matched;
            }

            // ROLLUP and CUBE add rows that total several groups.
            for (var i = 0; i < grouping.Rows.Count; i++)
            {
                if (grouping.Collapses && grouping.Sources[i] < 0)
                {
                    _hidden.Remove(scene._summaryIds[i]);
                    Emit([State(scene._summaryIds[i], VizState.New)], [], $"{grouping.SummaryRows} adds a row that totals several groups: {Calculated(grouping, i)}");
                    _settled[scene._summaryIds[i]] = VizState.Matched;
                }
            }

            if (grouping.Having is { } having)
                WriteTest("HAVING", having, scene._summaryIds, scene._havingCardIds, scene._having, _ => VizState.Matched);
        }

        private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

        private static string SummaryPhase(TraceGrouping grouping, int group, int rows, int result)
        {
            var label = grouping.Labels[group];
            return grouping.Collapses
                ? $"{label}: {Count(rows, "row")} become one row: {Calculated(grouping, result)}"
                : $"{label}: the window functions run over {Count(rows, "row")}, and each row keeps its place";
        }

        /// <summary>What was worked out for a row, such as "PaymentSum = 1200".</summary>
        private static string Calculated(TraceGrouping grouping, int row)
        {
            var columns = grouping.Calculated.Count > 0 ? grouping.Calculated : Enumerable.Range(0, grouping.Columns.Count).ToList();
            return string.Join(", ", columns.Select(c => $"{grouping.Columns[c]} = {grouping.Rows[row][c]}"));
        }

        // ----- ORDER BY -----

        private void WriteSort(TraceSort sort)
        {
            var ids = scene._sortedIds;
            for (var i = 0; i < ids.Count; i++)
            {
                var from = scene.SortSource(sort.Order[i]);
                _hidden.Remove(ids[i]);
                Emit([State(from, VizState.Active), State(ids[i], VizState.New)], [scene._sortedEdges[i]], $"Placing row {i + 1} of {ids.Count}");
                _settled[from] = VizState.Placed;
                _settled[ids[i]] = VizState.Placed;
            }

            Emit([], [], "Sorted");
        }

        // ----- DISTINCT and TOP -----

        private void WriteCuts()
        {
            var last = scene.LastTable();
            foreach (var cut in _trace.Cuts)
            {
                for (var i = 0; i < last.Count; i++)
                    _settled[last[i]] = cut.Keeps[i] ? VizState.Kept : VizState.Cut;
                Emit([], [], cut.Label);
            }

            if (_frames.Count == 0)
                Emit([], [], "No rows");
        }
    }
}
