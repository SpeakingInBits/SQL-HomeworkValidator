using Avalonia.Headless.XUnit;
using HomeworkValidator.App.Services;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.App.Views;
using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.App.Tests;

/// <summary>
/// Drives the workspace against a real SQL Server: set up databases, check a wrong and a right answer.
/// Runs only when HV_TEST_CONNECTION_STRING is set, e.g.
/// <c>Server=localhost;Integrated Security=true;TrustServerCertificate=true</c>.
/// </summary>
public class EndToEndTests
{
    [AvaloniaFact]
    public async Task Checks_an_answer_against_a_real_server()
    {
        var connectionString = Environment.GetEnvironmentVariable("HV_TEST_CONNECTION_STRING");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "HV_TEST_CONNECTION_STRING isn't set.");

        var content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));
        var server = new ServerConnection(connectionString!);
        var workspace = new WorkspaceViewModel(content, server, await server.TestAsync(), new AnswerStore(), () => { });
        var main = new MainViewModel(content) { CurrentPage = workspace };

        await workspace.SetUpDatabasesAsync();
        Assert.True(workspace.DatabasesReady, workspace.Problem);

        var question = workspace.Assignments.SelectMany(a => a.Questions).Single(q => q.Question.Id == "mgs-hw1/q3");
        workspace.SelectedNode = question;

        // Wrong: includes products priced exactly 500 and 2000, which only the edge data has.
        question.Document.Text = File.ReadAllText(Path.Combine(content.Root, "assignments", "mgs-hw1", "q3.mutants", "inclusive-range.sql"));
        await workspace.CheckCommand.ExecuteAsync(null);

        Assert.Null(workspace.Problem);
        Assert.Equal(QuestionStatus.Failed, question.Status);
        Assert.Contains(question.Result!.Datasets, d => d.Title == "Edge-case data" && d.IsFailed && d.Unexpected is not null);

        var window = new MainWindow { DataContext = main, Width = 1200, Height = 900 };
        window.Show();
        TestAppBuilder.SaveScreenshot(window, "end-to-end-failed");

        // Right: the reference solution.
        question.Document.Text = question.Question.SolutionSql;
        await workspace.CheckCommand.ExecuteAsync(null);

        Assert.Equal(QuestionStatus.Passed, question.Status);
        Assert.All(question.Result!.Datasets, d => Assert.True(d.IsPassed));
        TestAppBuilder.SaveScreenshot(window, "end-to-end-passed");
    }
}
