using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using HomeworkValidator.App.Controls;
using HomeworkValidator.App.Services;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.App.Views;
using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.App.Tests;

public class WindowTests
{
    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    private static MainWindow Show(MainViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1200, Height = 800 };
        window.Show();
        return window;
    }

    private static IEnumerable<string> VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    [AvaloniaFact]
    public void Connection_page_is_shown_first()
    {
        var window = Show(new MainViewModel(Content));

        TestAppBuilder.SaveScreenshot(window, "connection");
        Assert.Contains("Connect to your SQL Server", VisibleTexts(window));
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Connect");
    }

    [AvaloniaFact]
    public void Content_error_is_shown_instead_of_the_app()
    {
        var window = Show(new MainViewModel(null, "The homework content couldn't be loaded: test"));

        Assert.Contains("The homework content couldn't be loaded: test", VisibleTexts(window));
        Assert.DoesNotContain("Connect to your SQL Server", VisibleTexts(window));
    }

    [AvaloniaFact]
    public void Workspace_shows_the_question_and_a_failed_result()
    {
        var viewModel = new MainViewModel(Content);
        // Nothing connects until a command runs, so a made-up server is fine for layout.
        var workspace = new WorkspaceViewModel(Content, ServerConnection.Create("example-server", "sa", "x"),
            new ServerInfo("17.0.1000.7", "Developer Edition (64-bit)", true), new AnswerStore(TestAppBuilder.NewTempFolder()), () => { });
        viewModel.CurrentPage = workspace;

        var question = workspace.Assignments.SelectMany(a => a.Questions).Single(q => q.Question.Id == "mgs-hw1/q3");
        workspace.SelectedNode = question;
        question.Document.Text = "SELECT ProductName, ListPrice, DateAdded\nFROM Products\nWHERE ListPrice >= 500 AND ListPrice <= 2000\nORDER BY DateAdded DESC;";
        question.Result = new ResultViewModel(FailedReport(question.Question));

        var window = Show(viewModel);
        TestAppBuilder.SaveScreenshot(window, "workspace-failed");

        var texts = VisibleTexts(window).ToList();
        Assert.Contains("Question 3: Products priced between 500 and 2000", texts);
        Assert.Contains("Not quite", texts);
        Assert.Contains("Edge-case data", texts);
        Assert.Contains("Edge Guitar 500", texts); // a cell in the "not expected" table
        Assert.Contains(window.GetLogicalDescendants().OfType<ResultGrid>(), t => t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Whole_results_are_shown_side_by_side_when_expanded()
    {
        var question = Content.GetQuestion("mgs-hw1/q3");
        var columns = new[] { "ProductName", "ListPrice", "DateAdded" };
        var actual = new RowSample(columns,
        [
            ["Edge Bass 2000", "2000", "2016-08-02 10:00:00"],
            ["Edge Guitar 500", "500", "2016-08-01 10:00:00"],
            ["Gibson SG", "1208.42", "2016-02-05 11:04:31"],
        ], 3);
        var expected = new RowSample(columns, [["Gibson SG", "1208.42", "2016-02-05 11:04:31"]], 1);
        var report = new CheckReport(question, [], [], [],
        [
            new DatasetResult("edge", DatasetOutcome.Failed, "Expected 1 row, but your query returned 3 rows.",
                new ComparisonResult(false, "", Unexpected: new RowSample(columns, actual.Rows.Take(2).ToList(), 2))
                    { Actual = actual, Expected = expected },
                null),
        ]);

        var window = new Window { Content = new ResultView { DataContext = new ResultViewModel(report) }, Width = 1100, Height = 600 };
        window.Show();
        var expander = window.GetVisualDescendants().OfType<Expander>().Single();
        Assert.True(expander.IsEffectivelyVisible);
        Assert.Equal("Show your whole result next to the expected one", expander.Header);

        expander.IsExpanded = true;
        window.UpdateLayout();
        TestAppBuilder.SaveScreenshot(window, "result-full-side-by-side");

        var texts = VisibleTexts(window).ToList();
        Assert.Contains("Your result (3)", texts);
        Assert.Contains("Expected result (1)", texts);
    }

    [AvaloniaFact]
    public void Wide_results_scroll_sideways_on_both_sides()
    {
        var viewModel = new MainViewModel(Content);
        var workspace = new WorkspaceViewModel(Content, ServerConnection.Create("example-server", "sa", "x"),
            new ServerInfo("17.0.1000.7", "Developer Edition (64-bit)", true), new AnswerStore(TestAppBuilder.NewTempFolder()), () => { });
        viewModel.CurrentPage = workspace;
        var question = workspace.Assignments.SelectMany(a => a.Questions).Single(q => q.Question.Id == "mgs-hw1/q3");
        workspace.SelectedNode = question;

        // Twelve columns: far wider than either half of the side-by-side view.
        var columns = Enumerable.Range(1, 12).Select(i => $"Column{i}").ToArray();
        var rows = Enumerable.Range(1, 4).Select(r => (IReadOnlyList<string>)columns.Select(c => $"{c} value in row {r}").ToList()).ToList();
        question.Result = new ResultViewModel(new CheckReport(question.Question, [], [], [],
        [
            new DatasetResult("edge", DatasetOutcome.Failed, "Expected 3 rows, but your query returned 4 rows.",
                new ComparisonResult(false, "", Unexpected: new RowSample(columns, rows.Skip(3).ToList(), 1))
                    { Actual = new RowSample(columns, rows, 4), Expected = new RowSample(columns, rows.Take(3).ToList(), 3) },
                null),
        ]));

        var window = Show(viewModel);
        var expander = window.GetVisualDescendants().OfType<Expander>().Single();
        expander.IsExpanded = true;

        // The normal size, then narrower, as when the student resizes the window.
        foreach (var width in new[] { 1200, 900 })
        {
            window.Width = width;
            window.UpdateLayout();
            expander.BringIntoView();
            window.UpdateLayout();
            TestAppBuilder.SaveScreenshot(window, $"result-wide-{width}");

            var grids = expander.GetVisualDescendants().OfType<ResultGrid>().ToList();
            Assert.Equal(2, grids.Count);
            var yours = grids[0].TranslatePoint(default, window)!.Value;
            var expected = grids[1].TranslatePoint(default, window)!.Value;
            Assert.Equal(yours.Y, expected.Y); // still side by side
            Assert.True(yours.X < expected.X);

            var sectionRight = expander.TranslatePoint(new Point(expander.Bounds.Width, 0), window)!.Value.X;
            foreach (var grid in grids)
            {
                // The grid stays inside the section; what doesn't fit scrolls instead of running past the edge.
                Assert.True(grid.TranslatePoint(new Point(grid.Bounds.Width, 0), window)!.Value.X <= sectionRight);

                var scroller = grid.GetVisualDescendants().OfType<ScrollViewer>().Single();
                Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

                // A full-size scroll bar under the rows, not a thin line drawn over the last one.
                var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
                var rowsArea = scroller.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
                Assert.True(bar.IsEffectivelyVisible);
                Assert.True(bar.TranslatePoint(default, scroller)!.Value.Y >= rowsArea.TranslatePoint(new Point(0, rowsArea.Bounds.Height), scroller)!.Value.Y);

                // Scrolled all the way, the last column is in full view.
                var last = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Column12");
                Assert.False(InView(last, scroller));
                scroller.Offset = new Vector(scroller.Extent.Width - scroller.Viewport.Width, 0);
                window.UpdateLayout();
                Assert.True(InView(last, scroller));
                scroller.Offset = default;
            }
        }

        static bool InView(Control control, ScrollViewer scroller)
        {
            var left = control.TranslatePoint(default, scroller)!.Value.X;
            return left >= 0 && left + control.Bounds.Width <= scroller.Viewport.Width;
        }
    }

    [AvaloniaFact]
    public void Workspace_shows_the_linked_file_and_asks_when_both_changed()
    {
        var viewModel = new MainViewModel(Content);
        var workspace = new WorkspaceViewModel(Content, ServerConnection.Create("example-server", "sa", "x"),
            new ServerInfo("17.0.1000.7", "Developer Edition (64-bit)", true), new AnswerStore(TestAppBuilder.NewTempFolder()), () => { });
        viewModel.CurrentPage = workspace;
        var question = workspace.Assignments.SelectMany(a => a.Questions).Single(q => q.Question.Id == "mgs-hw1/q3");
        workspace.SelectedNode = question;

        var file = Path.Combine(TestAppBuilder.NewTempFolder(), "my-answer.sql");
        File.WriteAllText(file, "SELECT ProductName FROM Products;");
        workspace.OpenFile(file);
        var window = Show(viewModel);

        Assert.Contains("Linked to my-answer.sql. Saving it in another editor updates your answer here.", VisibleTexts(window));

        question.Document.Text = "SELECT ProductName, ListPrice FROM Products;";
        File.WriteAllText(file, "SELECT ProductName, DateAdded FROM Products;");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
        workspace.RefreshLinkedFiles();
        TestAppBuilder.SaveScreenshot(window, "workspace-linked-file-changed");

        Assert.Contains("my-answer.sql was saved, but you also changed your answer here. Which one do you want?", VisibleTexts(window));
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Load the file" && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Solution_is_offered_only_when_the_assignment_allows_it()
    {
        // MyGuitarShop homework 1 allows solutions, with an extra accepted answer for question 3; everything else keeps the defaults.
        var content = new ContentLibrary
        {
            Root = Content.Root,
            Databases = Content.Databases,
            Assignments = Content.Assignments.Select(a => a.Id != "mgs-hw1" ? a : a with
            {
                Questions = a.Questions.Select(q => q.Number != 3 ? q : q with
                {
                    Alternatives = [new NamedScript("between", "SELECT ProductName, ListPrice, DateAdded FROM Products\nWHERE ListPrice BETWEEN 500.01 AND 1999.99\nORDER BY DateAdded DESC;")],
                }).ToList(),
                SolutionsFrom = DateOnly.MinValue,
            }).ToList(),
        };
        var viewModel = new MainViewModel(content);
        var workspace = new WorkspaceViewModel(content, ServerConnection.Create("example-server", "sa", "x"),
            new ServerInfo("17.0.1000.7", "Developer Edition (64-bit)", true), new AnswerStore(TestAppBuilder.NewTempFolder()), () => { });
        viewModel.CurrentPage = workspace;
        var questions = workspace.Assignments.SelectMany(a => a.Questions).ToList();
        var window = Show(viewModel);
        ToggleButton ShowSolution() => window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Content as string == "Show solution");

        workspace.SelectedNode = questions.Single(q => q.Question.Id == "ap-hw1/q2");
        window.UpdateLayout();
        Assert.False(ShowSolution().IsEffectivelyVisible);

        var question = questions.Single(q => q.Question.Id == "mgs-hw1/q3");
        workspace.SelectedNode = question;
        window.UpdateLayout();
        Assert.True(ShowSolution().IsEffectivelyVisible);
        Assert.DoesNotContain("Solution", VisibleTexts(window));

        ShowSolution().IsChecked = true;
        window.UpdateLayout();
        TestAppBuilder.SaveScreenshot(window, "workspace-solution");

        var texts = VisibleTexts(window).ToList();
        Assert.Contains("Solution", texts);
        Assert.Contains(question.Question.SolutionSql.Trim(), texts);
        Assert.Contains("These answers are also accepted:", texts);
    }

    private static CheckReport FailedReport(Question question)
    {
        var columns = new[] { "ProductName", "ListPrice", "DateAdded" };
        var unexpected = new RowSample(columns,
        [
            ["Edge Guitar 500", "500", "2016-08-01 10:00:00"],
            ["Edge Bass 2000", "2000", "2016-08-02 10:00:00"],
        ], 2);

        return new CheckReport(question, [], [], ["Ignored 'USE MyGuitarShop': the validator runs your query against its own copy of the database."],
        [
            new DatasetResult("base", DatasetOutcome.Passed, "Matches the expected result.", ComparisonResult.Pass, null),
            new DatasetResult("edge", DatasetOutcome.Failed,
                "Expected 6 rows, but your query returned 8 rows. 2 rows in your result aren't in the expected result.",
                new ComparisonResult(false, "", Unexpected: unexpected),
                "Check products priced exactly 500 and exactly 2000. \"Greater than\" and \"less than\" don't include them."),
            new DatasetResult("shifted", DatasetOutcome.Passed, "Matches the expected result.", ComparisonResult.Pass, null),
        ]);
    }
}
