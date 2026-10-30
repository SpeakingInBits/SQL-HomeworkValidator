using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HomeworkValidator.Core.Comparison;

namespace HomeworkValidator.App.Controls;

/// <summary>
/// Shows a few result rows as a simple grid. The columns differ for every question, so the grid is built
/// in code from <see cref="Sample"/> instead of declared in XAML.
/// </summary>
public class ResultGrid : UserControl
{
    public static readonly StyledProperty<RowSample?> SampleProperty =
        AvaloniaProperty.Register<ResultGrid, RowSample?>(nameof(Sample));

    private const double MaxCellWidth = 320;

    public RowSample? Sample
    {
        get => GetValue(SampleProperty);
        set => SetValue(SampleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SampleProperty)
            Content = Sample is { } sample ? Build(sample) : null;
    }

    private static Control Build(RowSample sample)
    {
        var grid = new Grid();
        foreach (var _ in sample.Columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var i = 0; i <= sample.Rows.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var c = 0; c < sample.Columns.Count; c++)
            Add(grid, 0, c, sample.Columns[c], header: true);
        for (var r = 0; r < sample.Rows.Count; r++)
        {
            for (var c = 0; c < sample.Columns.Count; c++)
                Add(grid, r + 1, c, sample.Rows[r][c], header: false);
        }

        // Left and top edges; each cell draws its own right and bottom edges.
        var frame = new Border { BorderThickness = new Thickness(1, 1, 0, 0), Child = grid };
        BindResource(frame, Border.BorderBrushProperty, "DividerBrush");

        // Results wider than the space they get scroll sideways. AllowAutoHide is off because the Fluent theme's
        // auto-hiding scroll bar is a thin line drawn over the last row until the mouse is on it, so students
        // didn't see that there was more to scroll to. Without it the bar is full size and sits below the rows.
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = frame,
        };
    }

    private static void Add(Grid grid, int row, int column, string text, bool header)
    {
        var cell = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 3),
            Child = new TextBlock
            {
                Text = text,
                MaxWidth = MaxCellWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                FontStyle = !header && text == "NULL" ? FontStyle.Italic : FontStyle.Normal,
            },
        };
        BindResource(cell, Border.BorderBrushProperty, "DividerBrush");
        if (header)
            BindResource(cell, Border.BackgroundProperty, "PanelBrush");

        // Long values are cut off; hovering shows the whole value.
        if (text.Length > 40)
            ToolTip.SetTip(cell, text);

        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>The code equivalent of {DynamicResource key}: follows the light/dark theme.</summary>
    private static void BindResource(Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key));
}
