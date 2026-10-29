using HomeworkValidator.App.Services;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.Tests;

public sealed class AnswerStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hv-answers-" + Guid.NewGuid().ToString("N"));
    private readonly Question _question = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory)).GetQuestion("ap-hw1/q2");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Answers_and_statuses_survive_a_restart()
    {
        var store = new AnswerStore(_root);
        store.SaveAnswer(_question, "SELECT 1;");
        store.SetStatus(_question, QuestionStatus.Passed);

        var reopened = new AnswerStore(_root);

        Assert.Equal("SELECT 1;", reopened.LoadAnswer(_question));
        Assert.Equal(QuestionStatus.Passed, reopened.GetStatus(_question));
    }

    [Fact]
    public void Unanswered_questions_start_empty_and_unchecked()
    {
        var store = new AnswerStore(_root);

        Assert.Equal("", store.LoadAnswer(_question));
        Assert.Equal(QuestionStatus.NotChecked, store.GetStatus(_question));
    }

    [Fact]
    public void A_damaged_status_file_is_ignored()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "status.json"), "{ not json");

        Assert.Equal(QuestionStatus.NotChecked, new AnswerStore(_root).GetStatus(_question));
    }
}
