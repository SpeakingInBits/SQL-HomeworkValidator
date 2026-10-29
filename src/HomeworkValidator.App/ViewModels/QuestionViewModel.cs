using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkValidator.App.Services;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.ViewModels;

public partial class AssignmentViewModel(Assignment assignment, AnswerStore answers) : ViewModelBase
{
    public string Title => assignment.Title;

    public IReadOnlyList<QuestionViewModel> Questions { get; } =
        assignment.Questions.Select(q => new QuestionViewModel(q, answers, assignment.ShowsSolutions(DateOnly.FromDateTime(DateTime.Now)))).ToList();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>One question: its prompt, the student's answer (an editor document) and the last result.</summary>
public partial class QuestionViewModel : ViewModelBase
{
    private bool _dirty;

    /// <param name="canShowSolution">Whether the instructor allows showing this assignment's solutions yet (showSolutions in assignment.yaml).</param>
    public QuestionViewModel(Question question, AnswerStore answers, bool canShowSolution = false)
    {
        Question = question;
        CanShowSolution = canShowSolution;
        Status = answers.GetStatus(question);
        Document = new TextDocument(answers.LoadAnswer(question));
        Document.TextChanged += (_, _) => _dirty = true;
    }

    public Question Question { get; }

    public string Title => $"{Question.Number}. {Question.Title}";
    public string Heading => $"Question {Question.Number}: {Question.Title}";
    public string Details => $"{Question.Id} · {Question.Database} database";
    public string? Note => Question.Note;

    /// <summary>The editor's document. AvaloniaEdit's TextEditor binds to it directly.</summary>
    public TextDocument Document { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph), nameof(IsPassed), nameof(IsFailed))]
    public partial QuestionStatus Status { get; set; }

    public bool IsPassed => Status == QuestionStatus.Passed;
    public bool IsFailed => Status == QuestionStatus.Failed;

    public string StatusGlyph => Status switch
    {
        QuestionStatus.Passed => "✓",
        QuestionStatus.Failed => "✗",
        _ => "•",
    };

    /// <summary>The result of the last check in this session, kept so switching questions doesn't lose it.</summary>
    [ObservableProperty]
    public partial ResultViewModel? Result { get; set; }

    /// <summary>
    /// The .sql file the answer was opened from in this session, if any. While it's set, saving the file in another editor
    /// (SSMS, VS Code) loads it into this one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked), nameof(LinkedFileName))]
    public partial string? LinkedFile { get; private set; }

    public bool IsLinked => LinkedFile is not null;
    public string? LinkedFileName => Path.GetFileName(LinkedFile);

    /// <summary>The linked file changed, but the student has also edited the answer here, so it wasn't loaded. They choose.</summary>
    [ObservableProperty]
    public partial bool LinkedFileChanged { get; private set; }

    private DateTime _linkedWriteTime;
    private string _textAsLoaded = "";
    private string? _changedFileText;

    /// <summary>Replaces the answer with a file's text and keeps watching the file.</summary>
    public void OpenFile(string path)
    {
        var text = File.ReadAllText(path);
        LinkedFile = path;
        _linkedWriteTime = File.GetLastWriteTimeUtc(path);
        Load(text);
    }

    /// <summary>
    /// Loads the linked file again if it was saved since it was last read. If the student has edited the answer here since then,
    /// it asks instead (<see cref="LinkedFileChanged"/>). Called every second, and before checking.
    /// </summary>
    public void RefreshLinkedFile()
    {
        if (LinkedFile is not { } path)
            return;

        string text;
        try
        {
            // A missing file is often an editor saving by delete-and-rename; it's picked up once it's back.
            if (!File.Exists(path))
                return;
            var writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime == _linkedWriteTime)
                return;
            text = File.ReadAllText(path);
            _linkedWriteTime = writeTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Probably still being written; try again next time.
            return;
        }

        if (Document.Text == _textAsLoaded)
            Load(text);
        else if (text != Document.Text)
        {
            _changedFileText = text;
            LinkedFileChanged = true;
        }
    }

    /// <summary>The student chose the file's new version over their edits here.</summary>
    public void LoadChangedFile()
    {
        if (_changedFileText is { } text)
            Load(text);
    }

    /// <summary>The student chose to keep their edits here. A later save of the file asks again.</summary>
    public void KeepEdits()
    {
        _changedFileText = null;
        LinkedFileChanged = false;
    }

    public void Unlink()
    {
        LinkedFile = null;
        KeepEdits();
    }

    private void Load(string text)
    {
        Document.Text = text;
        _textAsLoaded = Document.Text;
        _changedFileText = null;
        LinkedFileChanged = false;
    }

    public bool CanShowSolution { get; }

    /// <summary>Set by the "Show solution" toggle.</summary>
    [ObservableProperty]
    public partial bool IsSolutionShown { get; set; }

    public string Solution => Question.SolutionSql.Trim();

    /// <summary>Other answers that are also accepted, for questions that can be read more than one way.</summary>
    public IReadOnlyList<string> Alternatives => Question.Alternatives.Select(a => a.Sql.Trim()).ToList();
    public bool HasAlternatives => Question.Alternatives.Count > 0;

    public void Save(AnswerStore answers)
    {
        if (!_dirty)
            return;
        answers.SaveAnswer(Question, Document.Text);
        _dirty = false;
    }
}
