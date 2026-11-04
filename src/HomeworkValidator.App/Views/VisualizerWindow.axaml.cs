using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeworkValidator.App.ViewModels;

namespace HomeworkValidator.App.Views;

/// <summary>Shows a query's animation in its own window, so it can sit next to the question or go on a classroom display.</summary>
public partial class VisualizerWindow : Window
{
    private const double ZoomStep = 1.2;

    public VisualizerWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as VisualizerViewModel)?.Pause();
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(ZoomStep);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / ZoomStep);
    private void Fit_Click(object? sender, RoutedEventArgs e) => Canvas.Fit();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
    }
}
