using Avalonia.Controls;
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
            new ServerInfo("17.0.1000.7", "Developer Edition (64-bit)", true), new AnswerStore(), () => { });
        viewModel.CurrentPage = workspace;

        var question = workspace.Assignments[1].Questions[2]; // mgs-hw1/q3
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
