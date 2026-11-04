namespace HomeworkValidator.Core.Visualization;

// A scene is a fixed graph of positioned nodes and edges (in world pixels) plus a list of animation frames.
// Each frame says how to draw the scene at that step. The canvas that draws it knows nothing about SQL.

public enum VizNodeKind
{
    /// <summary>A table's name above it.</summary>
    Title,

    /// <summary>A table's column headers.</summary>
    Header,

    Row,

    /// <summary>The heading of a group of rows.</summary>
    Group,

    /// <summary>A WHERE or HAVING condition.</summary>
    Card,
}

/// <summary>How a node looks at one step of the animation.</summary>
public enum VizState
{
    Default,

    /// <summary>Not read yet.</summary>
    Pending,

    /// <summary>Being tested.</summary>
    Scanning,

    /// <summary>Already read by a scan.</summary>
    Read,

    /// <summary>Part of the row being formed or moved.</summary>
    Active,

    /// <summary>Used or passed.</summary>
    Matched,

    /// <summary>Sorted into its final position.</summary>
    Placed,

    /// <summary>Removed by WHERE, or by a join that found no match.</summary>
    Rejected,

    /// <summary>Compared, and the ON condition was false.</summary>
    Miss,

    /// <summary>Just arrived in this table.</summary>
    New,

    /// <summary>Kept by DISTINCT or TOP.</summary>
    Kept,

    /// <summary>Removed by DISTINCT or TOP.</summary>
    Cut,

    /// <summary>Kept by an outer join with NULLs on the other side.</summary>
    Outer,

    /// <summary>Not part of the current step.</summary>
    Dim,

    /// <summary>A card whose condition is true for the current row.</summary>
    True,

    /// <summary>A card whose condition is false for the current row.</summary>
    False,
}

public enum VizEdgeKind
{
    /// <summary>A row moving from one table to the next.</summary>
    Path,

    /// <summary>In-depth join view: the ON condition is true for this pair of rows.</summary>
    Hit,

    /// <summary>In-depth join view: the ON condition is false for this pair of rows.</summary>
    Miss,
}

/// <summary>A positioned element on the canvas.</summary>
/// <param name="X">Left edge, in world pixels.</param>
/// <param name="Y">Top edge, in world pixels.</param>
/// <param name="Text">The text of a title or card.</param>
/// <param name="Cells">The cells of a row or header.</param>
/// <param name="CellWidths">The width of each cell, which all rows of a table share.</param>
public sealed record VizNode(
    string Id,
    VizNodeKind Kind,
    double X,
    double Y,
    double Width,
    double Height,
    string? Text = null,
    IReadOnlyList<string>? Cells = null,
    IReadOnlyList<double>? CellWidths = null)
{
    /// <summary>Cells to draw attention to, such as the ones an UPDATE changed.</summary>
    public IReadOnlyList<int> HighlightedCells { get; init; } = [];
}

/// <summary>A connector between two nodes. It's drawn only in frames that list it.</summary>
public sealed record VizEdge(string From, string To, VizEdgeKind Kind = VizEdgeKind.Path);

/// <summary>One step of the animation.</summary>
/// <param name="States">Nodes that don't look like <see cref="VizState.Default"/>.</param>
/// <param name="Hidden">Nodes not drawn in this step.</param>
/// <param name="Edges">Indexes into <see cref="VizScene.Edges"/> of the connectors to draw.</param>
/// <param name="Phase">Says what's happening, for students.</param>
public sealed record VizFrame(
    IReadOnlyDictionary<string, VizState> States,
    IReadOnlySet<string> Hidden,
    IReadOnlyList<int> Edges,
    string Phase);

/// <param name="DetailFrames">A longer alternative to <paramref name="Frames"/> for the same nodes and edges, showing every comparison a join makes.</param>
/// <param name="DetailUnavailable">Why a join doesn't have <paramref name="DetailFrames"/>.</param>
public sealed record VizScene(
    string Title,
    string Subtitle,
    IReadOnlyList<VizNode> Nodes,
    IReadOnlyList<VizEdge> Edges,
    IReadOnlyList<VizFrame> Frames,
    double Width,
    double Height,
    bool HasJoin = false,
    IReadOnlyList<VizFrame>? DetailFrames = null,
    string? DetailUnavailable = null);
