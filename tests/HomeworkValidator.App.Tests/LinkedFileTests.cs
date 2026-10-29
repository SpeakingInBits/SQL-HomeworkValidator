using HomeworkValidator.App.Services;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.Tests;

/// <summary>An answer opened from a .sql file follows the file when it's saved in another editor.</summary>
public sealed class LinkedFileTests
{
    private static readonly Question Question = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory)).GetQuestion("ap-hw1/q2");

    private readonly string _file = Path.Combine(TestAppBuilder.NewTempFolder(), "q2.sql");
    private readonly QuestionViewModel _question = new(Question, new AnswerStore(TestAppBuilder.NewTempFolder()));

    /// <summary>Saves the file as another editor would. The write time is moved on, since two quick saves can share one.</summary>
    private void SaveFile(string text)
    {
        var previous = File.Exists(_file) ? File.GetLastWriteTimeUtc(_file) : DateTime.MinValue;
        File.WriteAllText(_file, text);
        File.SetLastWriteTimeUtc(_file, previous == DateTime.MinValue ? DateTime.UtcNow : previous.AddSeconds(2));
    }

    [Fact]
    public void Opening_a_file_loads_it_and_links_it()
    {
        SaveFile("SELECT 1;");

        _question.OpenFile(_file);

        Assert.Equal("SELECT 1;", _question.Document.Text);
        Assert.True(_question.IsLinked);
        Assert.Equal("q2.sql", _question.LinkedFileName);
    }

    [Fact]
    public void Saving_the_file_reloads_the_answer()
    {
        SaveFile("SELECT 1;");
        _question.OpenFile(_file);

        SaveFile("SELECT 2;");
        _question.RefreshLinkedFile();

        Assert.Equal("SELECT 2;", _question.Document.Text);
        Assert.False(_question.LinkedFileChanged);
    }

    [Fact]
    public void Nothing_happens_until_the_file_is_saved()
    {
        SaveFile("SELECT 1;");
        _question.OpenFile(_file);
        _question.Document.Text = "SELECT 1 AS One;";

        _question.RefreshLinkedFile();

        Assert.Equal("SELECT 1 AS One;", _question.Document.Text);
        Assert.False(_question.LinkedFileChanged);
    }

    [Fact]
    public void Edits_here_are_kept_until_the_student_chooses()
    {
        SaveFile("SELECT 1;");
        _question.OpenFile(_file);
        _question.Document.Text = "SELECT 1 AS One;";

        SaveFile("SELECT 2;");
        _question.RefreshLinkedFile();

        Assert.Equal("SELECT 1 AS One;", _question.Document.Text);
        Assert.True(_question.LinkedFileChanged);

        _question.LoadChangedFile();

        Assert.Equal("SELECT 2;", _question.Document.Text);
        Assert.False(_question.LinkedFileChanged);
    }

    [Fact]
    public void Keeping_edits_asks_again_on_the_next_save()
    {
        SaveFile("SELECT 1;");
        _question.OpenFile(_file);
        _question.Document.Text = "SELECT 1 AS One;";
        SaveFile("SELECT 2;");
        _question.RefreshLinkedFile();

        _question.KeepEdits();

        Assert.Equal("SELECT 1 AS One;", _question.Document.Text);
        Assert.False(_question.LinkedFileChanged);

        SaveFile("SELECT 3;");
        _question.RefreshLinkedFile();

        Assert.True(_question.LinkedFileChanged);
    }

    [Fact]
    public void Unlinked_files_are_ignored()
    {
        SaveFile("SELECT 1;");
        _question.OpenFile(_file);

        _question.Unlink();
        SaveFile("SELECT 2;");
        _question.RefreshLinkedFile();

        Assert.False(_question.IsLinked);
        Assert.Equal("SELECT 1;", _question.Document.Text);
    }

    [Fact]
    public void A_file_being_replaced_is_picked_up_once_it_is_back()
    {
        SaveFile("SELECT 1;");
        _question.OpenFile(_file);

        File.Delete(_file);
        _question.RefreshLinkedFile();
        Assert.Equal("SELECT 1;", _question.Document.Text);

        SaveFile("SELECT 2;");
        _question.RefreshLinkedFile();
        Assert.Equal("SELECT 2;", _question.Document.Text);
    }
}
