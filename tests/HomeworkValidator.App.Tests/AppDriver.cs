using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.App.Views;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Drives the app the way a student would: finds controls by the text on screen, clicks them with the mouse
/// and types with the keyboard. Nothing here calls view-model commands directly, so a broken binding,
/// a disabled button or a key that doesn't reach its handler fails the test just as it would fail a student.
/// </summary>
public sealed class AppDriver
{
    private int _screenshot;

    private AppDriver(MainWindow window, string name)
    {
        Window = window;
        Name = name;
    }

    public MainWindow Window { get; }

    /// <summary>Prefix for this run's screenshots.</summary>
    public string Name { get; }

    public static AppDriver Launch(MainViewModel viewModel, string name)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1200, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new AppDriver(window, name);
    }

    // ----- Finding things on screen -----

    /// <summary>Text of every visible text block, top to bottom.</summary>
    public IReadOnlyList<string> VisibleTexts => Visible<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();

    public bool Shows(string text) => VisibleTexts.Contains(text);
    public bool ShowsTextStartingWith(string prefix) => VisibleTexts.Any(t => t.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>True if the text starting with <paramref name="prefix"/> is drawn in the app's failure color.</summary>
    public bool ShowsAsFailure(string prefix)
    {
        var text = Find<TextBlock>(t => t.Text?.StartsWith(prefix, StringComparison.Ordinal) == true, $"text starting '{prefix}'");
        return Window.TryFindResource("FailBrush", Window.ActualThemeVariant, out var fail)
               && Equals((text.Foreground as Avalonia.Media.ISolidColorBrush)?.Color, (fail as Avalonia.Media.ISolidColorBrush)?.Color);
    }

    public Button Button(string label) => Find<Button>(b => b.Content as string == label, $"a button labeled '{label}'");
    public RadioButton RadioButton(string label) => Find<RadioButton>(b => b.Content as string == label, $"an option labeled '{label}'");

    /// <summary>The text box under a label, e.g. "Server" or "Password".</summary>
    public TextBox TextBox(string label)
    {
        var caption = Find<TextBlock>(t => t.Text == label, $"the label '{label}'");
        return caption.GetVisualParent()!.GetVisualChildren().OfType<TextBox>().Single();
    }

    /// <summary>A question or assignment in the tree, by the start of its title.</summary>
    public TreeViewItem TreeItem(string titleStart)
    {
        var text = Find<TextBlock>(t => t.Text?.StartsWith(titleStart, StringComparison.Ordinal) == true && t.FindAncestorOfType<TreeViewItem>() is not null,
            $"a tree item starting '{titleStart}'");
        return text.FindAncestorOfType<TreeViewItem>()!;
    }

    /// <summary>The status mark (✓, ✗ or •) next to a question in the tree.</summary>
    public string TreeMark(string titleStart) =>
        TreeItem(titleStart).GetVisualDescendants().OfType<TextBlock>().First(t => t.Text is "✓" or "✗" or "•").Text!;

    public TextEditor Editor => Find<TextEditor>(_ => true, "the SQL editor");

    /// <summary>The header of a section that opens and closes, such as "Show your whole result next to the expected one".</summary>
    public ToggleButton SectionHeader(string text) =>
        Find<Expander>(e => e.Header as string == text, $"a section headed '{text}'").GetVisualDescendants().OfType<ToggleButton>().First();

    /// <summary>The first text block showing exactly this text, e.g. a column name in a result.</summary>
    public TextBlock Text(string text) => Find<TextBlock>(t => t.Text == text, $"the text '{text}'");

    /// <summary>True if all of the control is inside the part of its result that's showing, not scrolled away or cut off.</summary>
    public bool IsScrolledIntoView(Control control)
    {
        var scroller = control.FindAncestorOfType<ScrollViewer>() ?? throw new InvalidOperationException($"{control.GetType().Name} isn't in anything that scrolls.");
        var topLeft = control.TranslatePoint(default, scroller)!.Value;
        return topLeft.X >= 0 && topLeft.X + control.Bounds.Width <= scroller.Viewport.Width;
    }

    // ----- Acting -----

    /// <summary>Moves the mouse to the middle of the control and clicks.</summary>
    public void Click(Control control)
    {
        var center = PointOn(control);
        Window.MouseMove(center);
        Window.MouseDown(center, MouseButton.Left);
        Window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Where the mouse goes to use the control: its middle, once it's on screen and nothing covers it.</summary>
    private Point PointOn(Control control)
    {
        // Scroll it into view, then let layout and rendering catch up so positions and hit testing are current.
        control.BringIntoView();
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Window.CaptureRenderedFrame()?.Dispose(); // hit testing uses the last rendered frame
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException($"{control.GetType().Name} isn't in the window.");

        // A student can only click what they can see. If something else is on top (a scrollbar, another panel),
        // the click would land there, so fail with a clear message instead of a confusing one later.
        if (Window.InputHitTest(center) is not Visual hit || (hit != control && !hit.GetVisualAncestors().Contains(control)))
        {
            var screenshot = Screenshot("blocked-click");
            throw new InvalidOperationException(
                $"Clicking the middle of the {control.GetType().Name} at {center} would hit {Describe(Window.InputHitTest(center))} instead. Screenshot: {screenshot}");
        }

        return center;
    }

    /// <summary>Drags the sideways scroll bar of the result the control is in all the way to the right.</summary>
    public void ScrollRight(Control inside)
    {
        var scroller = inside.FindAncestorOfType<ScrollViewer>() ?? throw new InvalidOperationException($"{inside.GetType().Name} isn't in anything that scrolls.");
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().SingleOrDefault(b => b.Orientation == Orientation.Horizontal && b.IsEffectivelyVisible)
                  ?? throw new InvalidOperationException($"There's no sideways scroll bar to drag. Screenshot: {Screenshot("no-scroll-bar")}");
        var start = PointOn(bar.GetVisualDescendants().OfType<Thumb>().Single());
        var end = bar.TranslatePoint(new Point(bar.Bounds.Width, bar.Bounds.Height / 2), Window)!.Value;

        Window.MouseMove(start);
        Window.MouseDown(start, MouseButton.Left);
        Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    public void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPressQwerty(key, modifiers);
        Window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    public void SelectAll() => Press(PhysicalKey.A, RawInputModifiers.Control);

    /// <summary>Types text into whatever has focus. Each line break is an Enter key press.</summary>
    public void Type(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                Press(PhysicalKey.Enter);
            if (lines[i].Length > 0)
                Window.KeyTextInput(lines[i]);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks into a text box, selects what's there and types over it.</summary>
    public void Fill(TextBox box, string text)
    {
        Click(box);
        SelectAll();
        if (text.Length == 0)
            Press(PhysicalKey.Backspace);
        else
            Type(text);
    }

    /// <summary>Clicks into the SQL editor, selects everything and types the query.</summary>
    public void WriteAnswer(string sql)
    {
        Click(Editor);
        SelectAll();
        Type(sql);
    }

    // ----- Waiting and evidence -----

    /// <summary>Lets the app work (database calls run in the background) until the condition holds.</summary>
    public async Task WaitUntil(Func<bool> condition, string description, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            await Task.Delay(50);
        }

        var screenshot = Screenshot("timeout");
        throw new TimeoutException(
            $"Waited {timeoutSeconds}s for {description}. Screen shows: {string.Join(" | ", VisibleTexts)}. Screenshot: {screenshot}");
    }

    /// <summary>Saves a numbered screenshot of the current screen, e.g. journey-02-wrong-answer.png.</summary>
    public string Screenshot(string step)
    {
        Dispatcher.UIThread.RunJobs();
        return TestAppBuilder.SaveScreenshot(Window, $"{Name}-{++_screenshot:00}-{step}");
    }

    private static string Describe(IInputElement? element) => element switch
    {
        null => "nothing",
        TextBlock text => $"the text '{text.Text}'",
        _ => $"a {element.GetType().Name}",
    };

    private IEnumerable<T> Visible<T>() where T : Control =>
        Window.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible);

    private T Find<T>(Func<T, bool> match, string description) where T : Control =>
        Visible<T>().FirstOrDefault(match)
        ?? throw new InvalidOperationException($"Couldn't find {description}. Screen shows: {string.Join(" | ", VisibleTexts)}");
}
