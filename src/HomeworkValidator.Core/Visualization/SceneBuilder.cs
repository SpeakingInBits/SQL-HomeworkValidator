namespace HomeworkValidator.Core.Visualization;

/// <summary>
/// Lays a <see cref="QueryTrace"/> out as tables from left to right, one per step of the query, and writes the animation
/// frames that move rows between them. Pure layout: no SQL.
/// </summary>
public sealed class SceneBuilder
{
    private const double RowHeight = 28, HeaderHeight = 30, TitleHeight = 26, RowGap = 6, TableGap = 140, Pad = 48, CardHeight = 44, CardGap = 12;
    private const double CharWidth = 7.3, CellPadding = 18, MinCellWidth = 48, MaxCellWidth = 200, MinTableWidth = 120;

    private readonly QueryTrace _trace;
    private readonly List<VizNode> _nodes = [];
    private readonly List<VizEdge> _edges = [];

    private readonly List<List<string>> _sourceIds = [];
    private readonly List<string> _cardIds = [];

    /// <summary>The joined rows, or the rows of the only table: one node per row of the trace.</summary>
    private List<string> _rowIds = [];

    /// <summary>Rows that passed WHERE, and each trace row's position among them.</summary>
    private List<string> _matchedIds = [];
    private readonly Dictionary<int, int> _matchedSlot = [];
    private readonly Dictionary<int, int> _matchedEdge = [];

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

    public static VizScene Build(QueryTrace trace) => new SceneBuilder(trace).Build();

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
        var hasCards = _trace.Filter is not null;
        var y = Pad + (hasCards ? CardHeight + 28 : 0);
        var x = Pad;

        for (var k = 0; k < SourceCount; k++)
        {
            var source = _trace.Sources[k];
            var (ids, width) = AddTable($"t{k}", source.Label, source.Columns, source.Rows, x, y);
            _sourceIds.Add(ids);
            x += width + TableGap;
        }

        var filter = _trace.Filter;
        var sort = _trace.Sort;
        var everyColumn = _trace.Columns.Count;

        var inputTitle = HasJoin ? (filter is null ? "Joined rows" : "Joined rows (before WHERE)") : _trace.Title;
        var inputIsLast = filter is null && sort is null;
        var (rowIds, inputWidth) = AddTable("rows", inputTitle, Columns(inputIsLast ? _trace.ResultColumns : everyColumn),
            Rows(Enumerable.Range(0, RowCount), inputIsLast ? _trace.ResultColumns : everyColumn), x, y);
        _rowIds = rowIds;
        AddCards(x);
        x += inputWidth + TableGap;

        if (filter is not null)
        {
            var passing = Enumerable.Range(0, RowCount).Where(i => filter.Passes[i]).ToList();
            var columns = sort is null ? _trace.ResultColumns : everyColumn;
            var (ids, width) = AddTable("matched", sort is null ? "Result (WHERE)" : "Matched (WHERE)", Columns(columns), Rows(passing, columns), x, y);
            _matchedIds = ids;
            for (var slot = 0; slot < passing.Count; slot++)
            {
                _matchedSlot[passing[slot]] = slot;
                _matchedEdge[passing[slot]] = _edges.Count;
                _edges.Add(new VizEdge(_rowIds[passing[slot]], ids[slot]));
            }

            x += width + TableGap;
        }

        if (sort is not null)
        {
            var (ids, _) = AddTable("sorted", $"Sorted by {string.Join(", ", sort.Keys)}", Columns(_trace.ResultColumns), Rows(sort.Order, _trace.ResultColumns), x, y);
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

    private IReadOnlyList<string> Columns(int count) => _trace.Columns.Take(count).ToList();

    private List<IReadOnlyList<string>> Rows(IEnumerable<int> rows, int columns) =>
        rows.Select(i => (IReadOnlyList<string>)_trace.Rows[i].Take(columns).ToList()).ToList();

    /// <summary>The node a row is sorted from: the table just before the sorted one.</summary>
    private string SortSource(int row) => _trace.Filter is null ? _rowIds[row] : _matchedIds[_matchedSlot[row]];

    private (List<string> Ids, double Width) AddTable(
        string prefix, string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, double x, double y)
    {
        var widths = columns.Select((column, c) => CellWidth(rows.Select(r => r[c].Length).Append(column.Length).Max())).ToList();
        var width = Math.Max(widths.Sum(), MinTableWidth);

        // A narrow table's title can run on over the gap after it.
        _nodes.Add(new VizNode($"{prefix}-title", VizNodeKind.Title, x, y, Math.Max(width, title.Length * CharWidth + CellPadding), TitleHeight, Text: title));
        _nodes.Add(new VizNode($"{prefix}-header", VizNodeKind.Header, x, y + TitleHeight, width, HeaderHeight, Cells: columns, CellWidths: widths));

        var ids = new List<string>();
        var rowY = y + TitleHeight + HeaderHeight;
        for (var i = 0; i < rows.Count; i++)
        {
            var id = $"{prefix}-row-{i}";
            ids.Add(id);
            _nodes.Add(new VizNode(id, VizNodeKind.Row, x, rowY, width, RowHeight, Cells: rows[i], CellWidths: widths));
            rowY += RowHeight + RowGap;
        }

        return (ids, width);
    }

    private static double CellWidth(int characters) => Math.Clamp(characters * CharWidth + CellPadding, MinCellWidth, MaxCellWidth);

    /// <summary>One card per WHERE condition, across the top of the table it tests.</summary>
    private void AddCards(double x)
    {
        if (_trace.Filter is not { } filter)
            return;
        for (var c = 0; c < filter.Conditions.Count; c++)
        {
            var width = Math.Max(filter.Conditions[c].Length * CharWidth + 44, 150);
            var id = $"card-{c}";
            _cardIds.Add(id);
            _nodes.Add(new VizNode(id, VizNodeKind.Card, x, Pad, width, CardHeight, Text: filter.Conditions[c]));
            x += width + CardGap;
        }
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
        if (_trace.Sort is { } sort)
            parts.Add($"ORDER BY {string.Join(", ", sort.Keys)}");
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "A full table scan";
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
            _hidden.UnionWith(scene._matchedIds);
            _hidden.UnionWith(scene._sortedIds);

            if (scene.HasJoin)
                WriteJoin();
            else if (_trace.Filter is null && _trace.Sort is null)
                WriteScan();
            else if (_trace.Filter is null)
                Emit([], [], $"Rows of {_trace.Title}, in the order they're stored");

            if (_trace.Filter is { } filter)
                WriteFilter(filter);
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

        // ----- WHERE -----

        private void WriteFilter(TraceFilter filter)
        {
            var ids = scene._rowIds;
            var count = ids.Count;

            if (!scene.HasJoin)
            {
                foreach (var id in ids)
                    _settled[id] = VizState.Pending;
                Emit([], [], "Start scan");
            }

            for (var i = 0; i < count; i++)
            {
                var testing = State(ids[i], VizState.Scanning);
                Emit([testing], [], $"WHERE: test row {i + 1} of {count}");

                var cards = new List<KeyValuePair<string, VizState>> { testing };
                for (var c = 0; c < filter.Conditions.Count; c++)
                {
                    cards.Add(State(scene._cardIds[c], filter.ConditionResults[c][i] ? VizState.True : VizState.False));
                    Emit(cards, [], $"Check: {filter.Conditions[c]}");
                }

                if (filter.Passes[i])
                {
                    var matched = scene._matchedIds[scene._matchedSlot[i]];
                    _hidden.Remove(matched);
                    _settled[ids[i]] = scene.HasJoin && IsOuter(_trace.Paths[i]) ? VizState.Outer : VizState.Matched;
                    Emit([State(matched, VizState.New)], [scene._matchedEdge[i]], "✓ Kept by WHERE");
                    _settled[matched] = VizState.Matched;
                }
                else
                {
                    _settled[ids[i]] = VizState.Rejected;
                    Emit([], [], "✗ Filtered out by WHERE");
                }
            }

            Emit([], [], $"Filter complete: {scene._matchedIds.Count} of {count} row(s) kept");
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
            var last = _trace.Sort is not null ? scene._sortedIds
                : _trace.Filter is not null ? scene._matchedIds
                : scene._rowIds;

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
