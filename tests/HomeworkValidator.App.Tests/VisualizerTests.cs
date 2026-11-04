using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeworkValidator.App.Controls;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.App.Views;
using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.App.Tests;

/// <summary>The visualizer window, with a made-up query so these run without a SQL Server.</summary>
public class VisualizerTests
{
    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    /// <summary>Vendors (Al, Bo) LEFT JOIN Invoices (x, y), where Al has both and Bo has neither.</summary>
    private static VizScene Join() => SceneBuilder.Build(new QueryTrace(
        "Vendors",
        [new TraceSource("Vendors", null, ["Name"], [Row("Al"), Row("Bo")]), new TraceSource("Invoices", null, ["Number"], [Row("x"), Row("y")])],
        [new TraceJoin("LEFT JOIN", "Vendors.ID = Invoices.VendorID", 0)],
        ["Name", "Number"], 2,
        [Row("Al", "x"), Row("Al", "y"), Row("Bo", "NULL")],
        [[0, 0], [0, 1], [1, QueryTrace.NoMatch]],
        new TraceFilter("Number IS NULL", ["Number IS NULL"], [[false, false, true]], [false, false, true]),
        null, [])
    {
        Comparisons = [[true, true], [false, false]],
    });

    private static VizScene Scan(int rows) => SceneBuilder.Build(new QueryTrace(
        "Vendors", [], [], ["Name"], 1,
        Enumerable.Range(1, rows).Select(i => Row($"Vendor {i}")).ToList(),
        Enumerable.Range(0, rows).Select(i => new[] { i }).ToList(),
        null, null, []));

    private static (VisualizerWindow Window, VisualizerCanvas Canvas) Open(VisualizerViewModel animation, ThemeVariant? theme = null)
    {
        var window = new VisualizerWindow { DataContext = animation, Width = 1000, Height = 640, RequestedThemeVariant = theme };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<VisualizerCanvas>().Single());
    }

    [AvaloniaFact]
    public void Steps_forward_and_back_and_stops_at_the_ends()
    {
        var animation = new VisualizerViewModel(Scan(2));

        Assert.False(animation.PreviousCommand.CanExecute(null));
        Assert.Equal("Step 1 of 4", animation.StepText);
        Assert.Equal("Ready to scan", animation.Phase);

        animation.NextCommand.Execute(null);
        Assert.Equal("Reading row 1 of 2", animation.Phase);
        Assert.True(animation.PreviousCommand.CanExecute(null));

        animation.Step = 3;
        Assert.False(animation.NextCommand.CanExecute(null));

        animation.RestartCommand.Execute(null);
        Assert.Equal(0, animation.Step);
    }

    [AvaloniaFact]
    public async Task Play_runs_to_the_end_and_stops()
    {
        var animation = new VisualizerViewModel(Scan(1)) { Speed = 10 };

        animation.PlayPauseCommand.Execute(null);
        Assert.True(animation.IsPlaying);
        Assert.Equal("Pause", animation.PlayLabel);

        for (var i = 0; i < 100 && animation.IsPlaying; i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(animation.IsPlaying);
        Assert.Equal("Scan complete", animation.Phase);
        Assert.Equal("Play", animation.PlayLabel);
    }

    [AvaloniaFact]
    public void Play_at_the_end_starts_over()
    {
        var animation = new VisualizerViewModel(Scan(1));
        animation.Step = animation.Scene.Frames.Count - 1;

        animation.PlayPauseCommand.Execute(null);

        Assert.Equal(0, animation.Step);
        animation.Pause();
    }

    [AvaloniaFact]
    public void Stepping_pauses_playback()
    {
        var animation = new VisualizerViewModel(Scan(3));
        animation.PlayPauseCommand.Execute(null);

        animation.NextCommand.Execute(null);

        Assert.False(animation.IsPlaying);
    }

    [AvaloniaFact]
    public void Switching_to_the_in_depth_view_starts_over_with_its_own_steps()
    {
        var animation = new VisualizerViewModel(Join());
        animation.Step = 3;

        animation.InDepth = true;

        Assert.Equal(0, animation.Step);
        Assert.StartsWith("Ready: test each Vendors row", animation.Phase);
        Assert.Equal($"Step 1 of {animation.Scene.DetailFrames!.Count}", animation.StepText);
        Assert.True(animation.NextCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Only_joins_offer_the_in_depth_view()
    {
        Assert.True(new VisualizerViewModel(Join()).ShowsInDepth);
        Assert.False(new VisualizerViewModel(Scan(1)).ShowsInDepth);
    }

    [AvaloniaFact]
    public void The_in_depth_view_says_why_it_isnt_available()
    {
        var scene = Join() with { DetailFrames = null, DetailUnavailable = "The in-depth view is for queries with one join." };

        var animation = new VisualizerViewModel(scene);

        Assert.False(animation.CanUseInDepth);
        Assert.Equal("The in-depth view is for queries with one join.", animation.InDepthTip);
    }

    [AvaloniaFact]
    public void A_note_appears_when_only_some_rows_are_shown()
    {
        var (window, _) = Open(new VisualizerViewModel(Scan(1), "This shows 200 of them."));

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "This shows 200 of them." && t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Closing_the_window_stops_playback()
    {
        var animation = new VisualizerViewModel(Scan(3));
        var (window, _) = Open(animation);
        animation.PlayPauseCommand.Execute(null);

        window.Close();

        Assert.False(animation.IsPlaying);
    }

    [AvaloniaFact]
    public void The_mouse_wheel_zooms_around_the_pointer()
    {
        var (window, canvas) = Open(new VisualizerViewModel(Scan(3)));
        var before = canvas.Zoom;
        var point = canvas.TranslatePoint(new Point(200, 200), window)!.Value;

        window.CaptureRenderedFrame()?.Dispose();
        window.MouseWheel(point, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.Zoom > before);
    }

    [AvaloniaFact]
    public void Fit_shows_a_long_table_that_starts_out_readable()
    {
        var (window, canvas) = Open(new VisualizerViewModel(Scan(100)));
        Assert.Equal(0.6, canvas.Zoom);

        canvas.Fit();

        Assert.True(canvas.Zoom < 0.6);
        TestAppBuilder.SaveScreenshot(window, "visualizer-fit-long-table");
    }

    [AvaloniaFact]
    public void A_new_animation_replaces_the_scene_without_failing()
    {
        var first = new VisualizerViewModel(Join());
        var (window, _) = Open(first);
        first.Step = first.Scene.Frames.Count - 1;
        Dispatcher.UIThread.RunJobs();

        window.DataContext = new VisualizerViewModel(Scan(2));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.CaptureRenderedFrame());
    }

    [AvaloniaFact]
    public void Draws_in_the_dark_theme_too()
    {
        var animation = new VisualizerViewModel(Join());
        var (window, _) = Open(animation, ThemeVariant.Dark);
        animation.InDepth = true;
        animation.Step = 1;
        Dispatcher.UIThread.RunJobs();

        TestAppBuilder.SaveScreenshot(window, "visualizer-dark-in-depth");
        animation.InDepth = false;
        animation.Step = animation.Scene.Frames.Count - 1;
        Dispatcher.UIThread.RunJobs();
        TestAppBuilder.SaveScreenshot(window, "visualizer-dark");
    }
}
