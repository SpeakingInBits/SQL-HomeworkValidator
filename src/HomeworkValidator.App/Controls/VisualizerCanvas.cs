using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.App.Controls;

/// <summary>
/// Draws one frame of a <see cref="VizScene"/>, and lets the student pan by dragging and zoom with the mouse wheel.
/// It draws straight onto the screen in <see cref="Render"/> rather than using a control per row: a scene can have
/// hundreds of rows with several cells each, and every step of the animation restyles them.
/// </summary>
public class VisualizerCanvas : Control
{
    public static readonly StyledProperty<VizScene?> SceneProperty = AvaloniaProperty.Register<VisualizerCanvas, VizScene?>(nameof(Scene));
    public static readonly StyledProperty<VizFrame?> FrameProperty = AvaloniaProperty.Register<VisualizerCanvas, VizFrame?>(nameof(Frame));

    private const double MinZoom = 0.1, MaxZoom = 4, ReadableZoom = 0.6, FontSize = 12, CellPadding = 8, Corner = 4;

    private readonly Dictionary<(string Text, double Width, bool Bold, IBrush Brush), FormattedText> _texts = [];
    private readonly Dictionary<int, Geometry> _edgePaths = [];
    private Dictionary<string, VizNode> _nodesById = [];
    private Palette? _palette;

    private double _zoom = 1, _panX, _panY;
    private bool _startPending = true;
    private Point? _dragFrom;

    static VisualizerCanvas() => AffectsRender<VisualizerCanvas>(SceneProperty, FrameProperty);

    public VisualizerCanvas()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _palette = null;
            _texts.Clear();
            InvalidateVisual();
        };
    }

    public VizScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public VizFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public double Zoom => _zoom;

    // ----- Zooming and panning -----

    /// <summary>Zooms around the middle of the view, for the toolbar's buttons.</summary>
    public void ZoomBy(double factor) => ZoomAt(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>Shows the whole scene, however small that makes it.</summary>
    public void Fit()
    {
        if (Scene is not { } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        _zoom = Math.Clamp(FitZoom(scene), MinZoom, MaxZoom);
        _panX = Math.Max(12, (Bounds.Width - scene.Width * _zoom) / 2);
        _panY = Math.Max(12, (Bounds.Height - scene.Height * _zoom) / 2);
        InvalidateVisual();
    }

    private double FitZoom(VizScene scene) => Math.Min(Bounds.Width / scene.Width, Bounds.Height / scene.Height) * 0.92;

    /// <summary>The first view of a scene: all of it if it's still readable that way, or else its top left corner.</summary>
    private void ShowStart()
    {
        if (Scene is not { } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        _startPending = false;
        Fit();
        if (_zoom >= ReadableZoom)
            return;
        _zoom = ReadableZoom;
        _panX = _panY = 12;
        FollowActiveRows();
    }

    private void ZoomAt(double factor, Point center)
    {
        var zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _panX = center.X - (center.X - _panX) / _zoom * zoom;
        _panY = center.Y - (center.Y - _panY) / _zoom * zoom;
        _zoom = zoom;
        InvalidateVisual();
    }

    /// <summary>Long tables don't fit on screen, so the view moves to keep the row being worked on in sight.</summary>
    private void FollowActiveRows()
    {
        if (Frame is not { } frame || Bounds.Width <= 0)
            return;

        var active = frame.States.Where(s => s.Value is VizState.Scanning or VizState.New or VizState.Active && !frame.Hidden.Contains(s.Key))
            .Select(s => _nodesById.GetValueOrDefault(s.Key)).OfType<VizNode>().ToList();
        if (active.Count == 0)
            return;

        var area = active.Select(n => new Rect(n.X, n.Y, n.Width, n.Height)).Aggregate((a, b) => a.Union(b));
        var view = new Rect(-_panX / _zoom, -_panY / _zoom, Bounds.Width / _zoom, Bounds.Height / _zoom);
        if (view.Contains(area))
            return;

        // A row that's arriving matters most when the whole step can't fit.
        if (area.Width > view.Width || area.Height > view.Height)
        {
            var newest = active.FirstOrDefault(n => frame.States[n.Id] == VizState.New) ?? active[^1];
            area = new Rect(newest.X, newest.Y, newest.Width, newest.Height);
        }

        _panX = Bounds.Width / 2 - area.Center.X * _zoom;
        _panY = Bounds.Height / 2 - area.Center.Y * _zoom;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            _nodesById = Scene?.Nodes.ToDictionary(n => n.Id) ?? [];
            _edgePaths.Clear();
            _texts.Clear();
            _startPending = true;
            ShowStart();
        }
        else if (change.Property == FrameProperty)
        {
            FollowActiveRows();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_startPending)
            ShowStart();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        var position = e.GetPosition(this);
        _panX += position.X - from.X;
        _panY += position.Y - from.Y;
        _dragFrom = position;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.Delta.Y > 0 ? 1.12 : 1 / 1.12, e.GetPosition(this));
        e.Handled = true;
    }

    // ----- Drawing -----

    public override void Render(DrawingContext context)
    {
        // Transparent still counts as drawn, so dragging works on the empty parts too.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Scene is not { } scene || Frame is not { } frame)
            return;

        _palette ??= new Palette(this);
        var view = new Rect(-_panX / _zoom, -_panY / _zoom, Bounds.Width / _zoom, Bounds.Height / _zoom);

        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_panX, _panY)))
        {
            foreach (var edge in frame.Edges)
                DrawEdge(context, scene, edge, _palette);

            foreach (var node in scene.Nodes)
            {
                if (frame.Hidden.Contains(node.Id) || !view.Intersects(new Rect(node.X, node.Y, node.Width, node.Height)))
                    continue;
                DrawNode(context, node, frame.States.GetValueOrDefault(node.Id), _palette);
            }
        }
    }

    private void DrawEdge(DrawingContext context, VizScene scene, int index, Palette palette)
    {
        if (!_edgePaths.TryGetValue(index, out var path))
            _edgePaths[index] = path = EdgePath(scene.Edges[index]);

        var pen = scene.Edges[index].Kind switch
        {
            VizEdgeKind.Hit => palette.HitEdge,
            VizEdgeKind.Miss => palette.MissEdge,
            _ => palette.PathEdge,
        };
        context.DrawGeometry(null, pen, path);
    }

    private Geometry EdgePath(VizEdge edge)
    {
        var (from, to) = (_nodesById[edge.From], _nodesById[edge.To]);
        var start = new Point(from.X + from.Width, from.Y + from.Height / 2);
        var end = new Point(to.X, to.Y + to.Height / 2);
        var bend = Math.Max(40, Math.Abs(end.X - start.X) * 0.4);

        var geometry = new StreamGeometry();
        using var line = geometry.Open();
        line.BeginFigure(start, isFilled: false);
        line.CubicBezierTo(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end);
        line.EndFigure(isClosed: false);
        return geometry;
    }

    private void DrawNode(DrawingContext context, VizNode node, VizState state, Palette palette)
    {
        var bounds = new Rect(node.X, node.Y, node.Width, node.Height);
        switch (node.Kind)
        {
            case VizNodeKind.Title:
                DrawText(context, node.Text!, new Rect(bounds.X + 2, bounds.Y, bounds.Width - 2, bounds.Height), bold: true, palette.Accent);
                break;
            case VizNodeKind.Header:
                context.DrawRectangle(palette.Panel, palette.Border, bounds, Corner, Corner);
                DrawCells(context, node, bold: true, palette, palette.Accent);
                break;
            case VizNodeKind.Group:
                context.DrawRectangle(state == VizState.Active ? palette.ActiveTint : palette.AccentTint, state == VizState.Active ? palette.ActiveBorder : palette.Border, bounds, Corner, Corner);
                DrawText(context, node.Text!, new Rect(bounds.X + CellPadding, bounds.Y, bounds.Width - CellPadding * 2, bounds.Height), bold: true, palette.Text);
                break;
            case VizNodeKind.Card:
                DrawCard(context, node, bounds, state, palette);
                break;
            default:
                DrawRow(context, node, bounds, state, palette);
                break;
        }
    }

    private void DrawCard(DrawingContext context, VizNode node, Rect bounds, VizState state, Palette palette)
    {
        var (border, tint, label) = state switch
        {
            VizState.True => (palette.PassBorder, palette.PassTint, "TRUE"),
            VizState.False => (palette.FailBorder, palette.FailTint, "FALSE"),
            _ => (palette.Border, palette.Panel, null),
        };

        context.DrawRectangle(tint, border, bounds, Corner, Corner);
        var textArea = new Rect(bounds.X + 10, bounds.Y + 4, bounds.Width - 20, label is null ? bounds.Height - 8 : 18);
        DrawText(context, node.Text!, textArea, bold: false, palette.Text);
        if (label is not null)
            DrawText(context, label, new Rect(bounds.X + 10, bounds.Y + 22, bounds.Width - 20, 18), bold: true, state == VizState.True ? palette.Pass : palette.Fail);
    }

    private void DrawRow(DrawingContext context, VizNode node, Rect bounds, VizState state, Palette palette)
    {
        var (fill, border, opacity) = state switch
        {
            VizState.Dim => (palette.Panel, palette.Border, 0.25),
            VizState.Pending => (palette.Panel, palette.Border, 0.45),
            VizState.Scanning => (palette.AccentTint, palette.AccentBorder, 1),
            VizState.Read => (palette.Panel, palette.Border, 0.85),
            VizState.Active => (palette.ActiveTint, palette.ActiveBorder, 1),
            VizState.Matched or VizState.Kept => (palette.PassTint, palette.PassBorder, 1),
            VizState.Placed => (palette.Panel, palette.PassBorder, 1),
            VizState.Rejected => (palette.Panel, palette.Border, 0.32),
            VizState.Miss => (palette.Panel, palette.FailBorder, 0.6),
            VizState.New => (palette.AccentTint, palette.AccentBorder, 1),
            VizState.Cut => (palette.Panel, palette.FailBorder, 0.3),
            VizState.Outer => (palette.OuterTint, palette.OuterBorder, 1),
            _ => (palette.Panel, palette.Border, 1.0),
        };

        using (context.PushOpacity(opacity))
        {
            context.DrawRectangle(fill, border, bounds, Corner, Corner);
            DrawCells(context, node, bold: false, palette, palette.Text);
            if (state is VizState.Rejected or VizState.Cut)
                context.DrawLine(palette.Strike, new Point(bounds.Left + 4, bounds.Center.Y), new Point(bounds.Right - 4, bounds.Center.Y));
        }
    }

    private void DrawCells(DrawingContext context, VizNode node, bool bold, Palette palette, IBrush brush)
    {
        var x = node.X;
        for (var c = 0; c < node.Cells!.Count; c++)
        {
            var width = node.CellWidths![c];
            var text = node.Cells[c];
            var highlighted = node.HighlightedCells.Contains(c);
            if (highlighted)
                context.FillRectangle(palette.Highlight, new Rect(x + 1, node.Y + 1, width - 2, node.Height - 2));
            DrawText(context, text, new Rect(x + CellPadding, node.Y, width - CellPadding * 1.5, node.Height), bold || highlighted, !bold && text == "NULL" ? palette.Muted : brush);
            x += width;
            if (c < node.Cells.Count - 1)
                context.DrawLine(palette.Border, new Point(x, node.Y + 1), new Point(x, node.Y + node.Height - 1));
        }
    }

    private void DrawText(DrawingContext context, string text, Rect area, bool bold, IBrush brush)
    {
        if (area.Width <= 0 || text.Length == 0)
            return;

        var key = (text, area.Width, bold, brush);
        if (!_texts.TryGetValue(key, out var formatted))
        {
            var typeface = new Typeface(_palette!.Font, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal);
            _texts[key] = formatted = new FormattedText(text.ReplaceLineEndings(" "), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, brush)
            {
                MaxTextWidth = area.Width,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
        }

        context.DrawText(formatted, new Point(area.X, area.Y + (area.Height - formatted.Height) / 2));
    }

    /// <summary>The brushes and pens for the current light or dark theme, from the Viz* resources in App.axaml.</summary>
    private sealed class Palette
    {
        public Palette(VisualizerCanvas canvas)
        {
            IBrush Find(string key) => canvas.TryFindResource(key, canvas.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
            static IBrush Tint(IBrush brush) => new SolidColorBrush(((ISolidColorBrush)brush).Color, 0.18);

            Text = Find("VizTextBrush");
            Muted = Find("MutedBrush");
            Panel = Find("PanelBrush");
            Accent = Find("VizAccentBrush");
            Pass = Find("PassBrush");
            Fail = Find("FailBrush");
            var active = Find("VizActiveBrush");
            var outer = Find("VizOuterBrush");

            AccentTint = Tint(Accent);
            PassTint = Tint(Pass);
            FailTint = Tint(Fail);
            ActiveTint = Tint(active);
            OuterTint = Tint(outer);

            // Strong enough to stand out on a row that has a tint of its own.
            Highlight = new SolidColorBrush(((ISolidColorBrush)active).Color, 0.5);

            Border = new Pen(Find("DividerBrush"));
            AccentBorder = new Pen(Accent, 2);
            PassBorder = new Pen(Pass, 1.5);
            FailBorder = new Pen(Fail, 1.5);
            ActiveBorder = new Pen(active, 1.5);
            OuterBorder = new Pen(outer, 1.5, DashStyle.Dash);
            Strike = new Pen(Fail);

            PathEdge = new Pen(Accent, 2.5);
            HitEdge = new Pen(Pass, 2.5);
            MissEdge = new Pen(new SolidColorBrush(((ISolidColorBrush)Fail).Color, 0.4), 1.25);

            Font = canvas.TryFindResource("CodeFont", canvas.ActualThemeVariant, out var font) && font is FontFamily family ? family : FontFamily.Default;
        }

        public IBrush Text { get; }
        public IBrush Muted { get; }
        public IBrush Panel { get; }
        public IBrush Accent { get; }
        public IBrush Pass { get; }
        public IBrush Fail { get; }
        public IBrush AccentTint { get; }
        public IBrush PassTint { get; }
        public IBrush FailTint { get; }
        public IBrush ActiveTint { get; }
        public IBrush OuterTint { get; }
        public IBrush Highlight { get; }
        public IPen Border { get; }
        public IPen AccentBorder { get; }
        public IPen PassBorder { get; }
        public IPen FailBorder { get; }
        public IPen ActiveBorder { get; }
        public IPen OuterBorder { get; }
        public IPen Strike { get; }
        public IPen PathEdge { get; }
        public IPen HitEdge { get; }
        public IPen MissEdge { get; }
        public FontFamily Font { get; }
    }
}
