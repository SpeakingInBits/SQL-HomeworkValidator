namespace HomeworkValidator.Core.Visualization;

/// <summary>Sizes and table layout that every kind of scene shares.</summary>
internal static class SceneLayout
{
    public const double RowHeight = 28, HeaderHeight = 30, TitleHeight = 26, RowGap = 6, TableGap = 140, Pad = 48, CardHeight = 44, CardGap = 12, CardArea = CardHeight + 28;
    public const double CharWidth = 7.3, CellPadding = 18, MinCellWidth = 48, MaxCellWidth = 200, MinTableWidth = 120;

    public static List<double> CellWidths(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows)
    {
        var list = rows.ToList();
        return columns.Select((column, c) => Math.Clamp(list.Select(r => r[c].Length).Append(column.Length).Max() * CharWidth + CellPadding, MinCellWidth, MaxCellWidth)).ToList();
    }

    /// <summary>Adds a table's title and column headers; returns its width and the height they take.</summary>
    public static (double Width, double Height) AddHeading(List<VizNode> nodes, string prefix, string title, IReadOnlyList<string> columns, IReadOnlyList<double> widths, double x, double y)
    {
        var width = Math.Max(widths.Sum(), MinTableWidth);

        // A narrow table's title can run on over the gap after it.
        nodes.Add(new VizNode($"{prefix}-title", VizNodeKind.Title, x, y, Math.Max(width, title.Length * CharWidth + CellPadding), TitleHeight, Text: title));
        nodes.Add(new VizNode($"{prefix}-header", VizNodeKind.Header, x, y + TitleHeight, width, HeaderHeight, Cells: columns, CellWidths: widths));
        return (width, TitleHeight + HeaderHeight);
    }

    /// <summary>Adds a titled table with one node per row; returns the rows' ids and the table's width.</summary>
    /// <param name="highlighted">Which cells of each row to draw attention to.</param>
    public static (List<string> Ids, double Width) AddTable(
        List<VizNode> nodes, string prefix, string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, double x, double y,
        Func<int, IReadOnlyList<int>>? highlighted = null)
    {
        var widths = CellWidths(columns, rows);
        var (width, heading) = AddHeading(nodes, prefix, title, columns, widths, x, y);

        var ids = new List<string>();
        var rowY = y + heading;
        for (var i = 0; i < rows.Count; i++)
        {
            var id = $"{prefix}-row-{i}";
            ids.Add(id);
            nodes.Add(new VizNode(id, VizNodeKind.Row, x, rowY, width, RowHeight, Cells: rows[i], CellWidths: widths) { HighlightedCells = highlighted?.Invoke(i) ?? [] });
            rowY += RowHeight + RowGap;
        }

        return (ids, width);
    }

    /// <summary>One card per condition, side by side from <paramref name="x"/>.</summary>
    public static List<string> AddCards(List<VizNode> nodes, string prefix, IReadOnlyList<string> conditions, double x)
    {
        var ids = new List<string>();
        for (var c = 0; c < conditions.Count; c++)
        {
            var width = Math.Max(conditions[c].Length * CharWidth + 44, 150);
            ids.Add($"{prefix}-{c}");
            nodes.Add(new VizNode(ids[c], VizNodeKind.Card, x, Pad, width, CardHeight, Text: conditions[c]));
            x += width + CardGap;
        }

        return ids;
    }
}
