using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Checking;

public enum DatasetOutcome
{
    Passed,
    Failed,

    /// <summary>The student's query failed to run (SQL error, timeout, too many rows).</summary>
    Error,
}

public sealed record DatasetResult(string Dataset, DatasetOutcome Outcome, string Message, ComparisonResult? Comparison, string? Hint);

public sealed record CheckReport(
    Question Question,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> RuleViolations,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DatasetResult> Datasets)
{
    public bool Passed => Errors.Count == 0 && RuleViolations.Count == 0 && Datasets.All(d => d.Outcome == DatasetOutcome.Passed);
}

/// <summary>Raised when a question's own content is broken (bad solution, bad compare options), as opposed to a wrong answer.</summary>
public sealed class QuestionConfigurationException(string message) : Exception(message);

/// <summary>Checks a student's SQL against a question by running it next to the reference solution on each dataset.</summary>
public sealed class Checker(ContentLibrary content, DatabaseManager databases, QueryRunner runner)
{
    private readonly Dictionary<(string Question, string Dataset), ResultTable> _expected = [];

    public async Task<CheckReport> CheckAsync(Question question, string studentSql, CancellationToken ct = default)
    {
        var analysis = ScriptAnalyzer.AnalyzeSelect(studentSql);
        var violations = ScriptAnalyzer.CheckRules(analysis.Features, question.Rules);

        if (!analysis.IsRunnable)
            return new CheckReport(question, analysis.Errors, violations, analysis.Notes, []);

        var database = content.GetDatabase(question.Database);
        var results = new List<DatasetResult>();
        foreach (var datasetName in question.Datasets)
        {
            var dataset = database.GetDataset(datasetName);
            await databases.EnsureAsync(database, dataset, ct);
            var dbName = DatabaseManager.DatabaseName(database, dataset);

            var expected = await GetExpectedAsync(question, dbName, dataset, ct);
            question.Hints.TryGetValue(dataset.Name, out var hint);

            ResultTable actual;
            try
            {
                actual = await runner.RunAsync(dbName, analysis.ExecutableSql!, restricted: true, ct);
            }
            catch (QueryFailedException ex)
            {
                results.Add(new DatasetResult(dataset.Name, DatasetOutcome.Error, ex.Message, null, null));
                continue;
            }

            ComparisonResult comparison;
            try
            {
                comparison = ResultComparer.Compare(expected, actual, question.Compare);
            }
            catch (ContentException ex)
            {
                throw new QuestionConfigurationException($"{question.Id}: {ex.Message}");
            }

            results.Add(comparison.Passed
                ? new DatasetResult(dataset.Name, DatasetOutcome.Passed, comparison.Message, comparison, null)
                : new DatasetResult(dataset.Name, DatasetOutcome.Failed, comparison.Message, comparison, hint));
        }

        return new CheckReport(question, [], violations, analysis.Notes, results);
    }

    /// <summary>Runs the reference solution, caching its result for the life of this checker.</summary>
    private async Task<ResultTable> GetExpectedAsync(Question question, string dbName, Dataset dataset, CancellationToken ct)
    {
        if (_expected.TryGetValue((question.Id, dataset.Name), out var cached))
            return cached;

        var analysis = ScriptAnalyzer.AnalyzeSelect(question.SolutionSql);
        if (!analysis.IsRunnable)
            throw new QuestionConfigurationException($"{question.Id}: the reference solution isn't valid: {string.Join(" ", analysis.Errors)}");

        try
        {
            var result = await runner.RunAsync(dbName, analysis.ExecutableSql!, restricted: false, ct);
            _expected[(question.Id, dataset.Name)] = result;
            return result;
        }
        catch (QueryFailedException ex)
        {
            throw new QuestionConfigurationException($"{question.Id}: the reference solution failed on '{dataset.Name}': {ex.Message}");
        }
    }
}
