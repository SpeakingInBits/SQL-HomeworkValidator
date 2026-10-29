using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.ViewModels;

/// <summary>A check's outcome, shaped for display.</summary>
public class ResultViewModel : ViewModelBase
{
    public ResultViewModel(CheckReport report)
    {
        Passed = report.Passed;
        Notes = report.Notes;
        Errors = report.Errors;
        RuleViolations = report.RuleViolations;
        Datasets = report.Datasets.Select(d => new DatasetResultViewModel(d, report.Question.IsScript)).ToList();

        Heading = Passed ? "Correct!" : "Not quite";
        Summary = (Errors.Count, Datasets.Count) switch
        {
            ( > 0, _) => "Your SQL couldn't be run. Fix the problem below and check again.",
            (_, 0) => "",
            (_, 1) when Passed => $"{What(report.Question.Kind)}.",
            (_, 1) => "See what's different below.",
            _ when Passed => $"{What(report.Question.Kind)} on all {Datasets.Count} datasets.",
            _ => $"{What(report.Question.Kind)} on {Datasets.Count(d => d.IsPassed)} of {Datasets.Count} datasets.",
        };
    }

    private static string What(QuestionKind kind) => kind switch
    {
        QuestionKind.Dml => "Your script leaves every table as expected",
        QuestionKind.Ddl => "Your design matches the expected one",
        _ => "Your query matches the expected result",
    };

    public bool Passed { get; }
    public bool Failed => !Passed;
    public string Heading { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Notes { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> RuleViolations { get; }

    // Empty lists are hidden so they don't add spacing.
    public bool HasNotes => Notes.Count > 0;
    public bool HasErrors => Errors.Count > 0;
    public bool HasRuleViolations => RuleViolations.Count > 0;
    public IReadOnlyList<DatasetResultViewModel> Datasets { get; }
}

/// <param name="isScript">True for DML questions, where the student wrote a script that changes tables rather than a query.</param>
public class DatasetResultViewModel(DatasetResult result, bool isScript = false) : ViewModelBase
{
    public string Title => result.Dataset switch
    {
        "base" => "Course data",
        "edge" => "Edge-case data",
        "shifted" => "Shifted-ID data",
        var name => $"{name} data",
    };

    public string Outcome => result.Outcome switch
    {
        DatasetOutcome.Passed => "PASS",
        DatasetOutcome.Failed => "FAIL",
        _ => "ERROR",
    };

    public bool IsPassed => result.Outcome == DatasetOutcome.Passed;
    public bool IsFailed => result.Outcome == DatasetOutcome.Failed;
    public bool IsError => result.Outcome == DatasetOutcome.Error;

    /// <summary>Hidden when passed: "Matches the expected result" adds nothing next to PASS.</summary>
    public string? Message => IsPassed ? null : IsError ? $"Your {(isScript ? "script" : "query")} failed to run: {result.Message}" : result.Message;

    public string? Hint => result.Hint;
    public RowSample? Missing => result.Comparison?.Missing;
    public RowSample? Unexpected => result.Comparison?.Unexpected;

    public string? MissingHeading => Missing is null ? null : Heading(Table is null ? "Missing from your result" : $"Missing from {Table}", Missing);
    public string? UnexpectedHeading => Unexpected is null ? null : Heading(Table is null ? "In your result but not expected" : $"In {Table} but not expected", Unexpected);

    /// <summary>For DML questions, the table the rows come from.</summary>
    private string? Table => result.Comparison?.Table;

    private static string Heading(string text, RowSample sample) =>
        sample.Total > sample.Rows.Count
            ? $"{text} (first {sample.Rows.Count} of {sample.Total})"
            : $"{text} ({sample.Total})";
}
