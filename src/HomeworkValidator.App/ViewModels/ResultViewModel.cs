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
        // Datasets that came out the same share a section, so each problem is shown once.
        Datasets = report.Groups.Select(g => new DatasetResultViewModel(g, report.Question.IsScript)).ToList();

        var checkedOn = report.Datasets.Count;
        var passedOn = report.Datasets.Count(d => d.Outcome == DatasetOutcome.Passed);
        Heading = Passed ? "Correct!" : "Not quite";
        Summary = (Errors.Count, checkedOn) switch
        {
            ( > 0, _) => "Your SQL couldn't be run. Fix the problem below and check again.",
            (_, 0) => "",
            (_, 1) when Passed => $"{What(report.Question.Kind)}.",
            (_, 1) => "See what's different below.",
            _ when Passed => $"{What(report.Question.Kind)} on all {checkedOn} datasets.",
            // The same problem on every dataset: counting them adds nothing.
            _ when passedOn == 0 && Datasets.Count == 1 => "See what's different below.",
            _ => $"{What(report.Question.Kind)} on {passedOn} of {checkedOn} datasets.",
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

/// <summary>One outcome and the datasets it happened on, e.g. "Course data and edge-case data".</summary>
/// <param name="isScript">True for DML questions, where the student wrote a script that changes tables rather than a query.</param>
public class DatasetResultViewModel(DatasetGroup group, bool isScript = false) : ViewModelBase
{
    private readonly DatasetResult _result = group.Result;

    public string Title => group.Title;

    public string Outcome => _result.Outcome switch
    {
        DatasetOutcome.Passed => "PASS",
        DatasetOutcome.Failed => "FAIL",
        _ => "ERROR",
    };

    public bool IsPassed => _result.Outcome == DatasetOutcome.Passed;
    public bool IsFailed => _result.Outcome == DatasetOutcome.Failed;
    public bool IsError => _result.Outcome == DatasetOutcome.Error;

    /// <summary>Hidden when passed: "Matches the expected result" adds nothing next to PASS.</summary>
    public string? Message => IsPassed ? null : IsError ? $"Your {(isScript ? "script" : "query")} failed to run: {_result.Message}" : _result.Message;

    public string? Hint => _result.Hint;
    public RowSample? Missing => _result.Comparison?.Missing;
    public RowSample? Unexpected => _result.Comparison?.Unexpected;

    public string? MissingHeading => Missing is null ? null : Heading(Table is null ? "Missing from your result" : $"Missing from {Table}", Missing);
    public string? UnexpectedHeading => Unexpected is null ? null : Heading(Table is null ? "In your result but not expected" : $"In {Table} but not expected", Unexpected);

    /// <summary>
    /// The student's whole result and the expected one, shown side by side. For scripts, the first table that differs.
    /// When several datasets share this section, these come from the first of them, and the header says which.
    /// </summary>
    public RowSample? Actual => _result.Comparison?.Actual;
    public RowSample? Expected => _result.Comparison?.Expected;
    public bool HasFullResults => Actual is not null && Expected is not null;

    public string FullResultsHeader
    {
        get
        {
            var on = group.Datasets.Count > 1 ? $" on {DatasetNames.Name(_result.Dataset)}" : "";
            return Table is null ? $"Show your whole result{on} next to the expected one" : $"Show all of {Table}{on} next to the expected rows";
        }
    }

    public string? ActualHeading => Actual is null ? null : Heading(Table is null ? "Your result" : $"{Table} after your script", Actual);
    public string? ExpectedHeading => Expected is null ? null : Heading(Table is null ? "Expected result" : $"Expected {Table}", Expected);

    /// <summary>For DML questions, the table the rows come from.</summary>
    private string? Table => _result.Comparison?.Table;

    private static string Heading(string text, RowSample sample) =>
        sample.Total > sample.Rows.Count
            ? $"{text} (first {sample.Rows.Count} of {sample.Total})"
            : $"{text} ({sample.Total})";
}
