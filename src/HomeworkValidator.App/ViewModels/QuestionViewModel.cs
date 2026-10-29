using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkValidator.App.Services;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.ViewModels;

public partial class AssignmentViewModel(Assignment assignment, AnswerStore answers) : ViewModelBase
{
    public string Title => assignment.Title;

    public IReadOnlyList<QuestionViewModel> Questions { get; } =
        assignment.Questions.Select(q => new QuestionViewModel(q, answers)).ToList();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>One question: its prompt, the student's answer (an editor document) and the last result.</summary>
public partial class QuestionViewModel : ViewModelBase
{
    private bool _dirty;

    public QuestionViewModel(Question question, AnswerStore answers)
    {
        Question = question;
        Status = answers.GetStatus(question);
        Document = new TextDocument(answers.LoadAnswer(question));
        Document.TextChanged += (_, _) => _dirty = true;
    }

    public Question Question { get; }

    public string Title => $"{Question.Number}. {Question.Title}";
    public string Heading => $"Question {Question.Number}: {Question.Title}";
    public string Details => $"{Question.Id} · {Question.Database} database";

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

    public void Save(AnswerStore answers)
    {
        if (!_dirty)
            return;
        answers.SaveAnswer(Question, Document.Text);
        _dirty = false;
    }
}
