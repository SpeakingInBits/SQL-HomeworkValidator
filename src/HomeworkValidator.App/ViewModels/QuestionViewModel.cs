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
