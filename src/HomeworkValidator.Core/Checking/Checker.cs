using HomeworkValidator.Core.Comparison;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Checking;

public enum DatasetOutcome
{
    Passed,
    Failed,

    /// <summary>The student's SQL failed to run (SQL error, timeout, too many rows).</summary>
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

/// <summary>
/// Checks a student's SQL against a question by running it next to the reference solution on each dataset.
/// SELECT questions compare result sets; DML questions compare every table afterwards.
/// </summary>
public sealed class Checker(ContentLibrary content, DatabaseManager databases, ServerConnection server)
{
    private readonly QueryRunner _queries = new(server);
    private readonly ScriptRunner _scripts = new(server);
    private readonly Dictionary<(string Question, string Reference, string Dataset), object> _expected = [];

    /// <summary>How many missing and unexpected rows each dataset result includes for display.</summary>
    public int SampleSize { get; init; } = ResultComparer.DefaultSampleSize;

    public async Task<CheckReport> CheckAsync(Question question, string studentSql, CancellationToken ct = default)
    {
        var analysis = ScriptAnalyzer.Analyze(studentSql, question.Kind);
        var violations = ScriptAnalyzer.CheckRules(analysis.Features, question.Rules);

        if (!analysis.IsRunnable)
            return new CheckReport(question, analysis.Errors, violations, analysis.Notes, []);

        var notes = analysis.Notes.ToList();
        var database = content.GetDatabase(question.Database);
        var results = new List<DatasetResult>();
        foreach (var datasetName in question.Datasets)
        {
            var dataset = database.GetDataset(datasetName);
            await databases.EnsureAsync(database, dataset, ct);
            var hint = question.Hints.GetValueOrDefault(dataset.Name) ?? dataset.Hint;

            ComparisonResult comparison;
            try
            {
                comparison = question.Kind == QuestionKind.Dml
                    ? await CheckDmlAsync(question, database, dataset, analysis.Batches, notes, ct)
                    : await CheckSelectAsync(question, dataset, DatabaseManager.DatabaseName(database, dataset), analysis.Batches[0], ct);
            }
            catch (QueryFailedException ex)
            {
                results.Add(new DatasetResult(dataset.Name, DatasetOutcome.Error, ex.Message, null, null));
                continue;
            }

            results.Add(comparison.Passed
                ? new DatasetResult(dataset.Name, DatasetOutcome.Passed, comparison.Message, comparison, null)
                : new DatasetResult(dataset.Name, DatasetOutcome.Failed, comparison.Message, comparison, hint));
        }

        return new CheckReport(question, [], violations, notes.Distinct().ToList(), results);
    }

    private async Task<ComparisonResult> CheckSelectAsync(Question question, Dataset dataset, string dbName, string query, CancellationToken ct)
    {
        var actual = await _queries.RunAsync(dbName, query, restricted: true, ct);

        ComparisonResult? first = null;
        foreach (var reference in References(question))
        {
            var expected = await GetExpectedAsync(question, reference, dataset, async () =>
            {
                var batches = ReferenceBatches(question, reference);
                return await _queries.RunAsync(dbName, batches[0], restricted: false, ct);
            });

            var comparison = Compare(question, () => ResultComparer.Compare(expected, actual, question.Compare, SampleSize));
            if (comparison.Passed)
                return comparison;
            first ??= comparison;
        }

        return first!;
    }

    private async Task<ComparisonResult> CheckDmlAsync(
        Question question, CourseDatabase database, Dataset dataset, IReadOnlyList<string> script, List<string> notes, CancellationToken ct)
    {
        var dbName = DatabaseManager.DatabaseName(database, dataset);
        var setup = SetupBatches(question);

        ScriptRun actual;
        try
        {
            actual = await _scripts.RunAsync(dbName, setup, script, restricted: true, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new QuestionConfigurationException($"{question.Id}: {ex.Message}");
        }

        if (actual.EndedTransaction)
        {
            // A COMMIT or ROLLBACK in the script ended the validator's transaction, so its changes may be saved for good.
            notes.Add("Your script's COMMIT or ROLLBACK ended the validator's own transaction. That's fine in your database, " +
                      "but the validator had to rebuild its copy afterwards.");
            await databases.RebuildAsync(database, dataset, ct);
        }

        ComparisonResult? first = null;
        foreach (var reference in References(question))
        {
            var expected = await GetExpectedAsync(question, reference, dataset, async () =>
            {
                try
                {
                    var run = await _scripts.RunAsync(dbName, setup, ReferenceBatches(question, reference), restricted: false, ct);
                    return run.Snapshot;
                }
                catch (InvalidOperationException ex)
                {
                    throw new QuestionConfigurationException($"{question.Id}: {ex.Message}");
                }
            });

            var comparison = Compare(question, () => StateComparer.Compare(expected, actual.Snapshot, question.Compare, SampleSize));
            if (comparison.Passed)
                return comparison;
            first ??= comparison;
        }

        return first!;
    }

    /// <summary>
    /// The batches that set up a DML question's starting state: the reference solutions of the questions it runs after
    /// (and of the ones those run after), then its own setup script.
    /// </summary>
    private IReadOnlyList<string> SetupBatches(Question question)
    {
        var batches = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Question q)
        {
            foreach (var id in q.After)
            {
                var earlier = content.GetQuestion(id);
                if (!done.Add(earlier.Id))
                    continue;
                Add(earlier);
                if (earlier.SetupSql is not null)
                    batches.AddRange(BatchSplitter.Split(earlier.SetupSql));
                batches.AddRange(ReferenceBatches(earlier, new NamedScript("solution", earlier.SolutionSql)));
            }
        }

        Add(question);
        if (question.SetupSql is not null)
            batches.AddRange(BatchSplitter.Split(question.SetupSql));
        return batches;
    }

    /// <summary>The main reference solution first, then any alternatives.</summary>
    private static IEnumerable<NamedScript> References(Question question) =>
        question.Alternatives.Prepend(new NamedScript("solution", question.SolutionSql));

    private static IReadOnlyList<string> ReferenceBatches(Question question, NamedScript reference)
    {
        var analysis = ScriptAnalyzer.Analyze(reference.Sql, question.Kind);
        if (!analysis.IsRunnable)
            throw new QuestionConfigurationException($"{question.Id}: the reference {reference.Name} isn't valid: {string.Join(" ", analysis.Errors)}");
        return analysis.Batches;
    }

    /// <summary>Runs a reference once per dataset and keeps the result for the life of this checker.</summary>
    private async Task<T> GetExpectedAsync<T>(Question question, NamedScript reference, Dataset dataset, Func<Task<T>> run) where T : class
    {
        var key = (question.Id, reference.Name, dataset.Name);
        if (_expected.TryGetValue(key, out var cached))
            return (T)cached;

        try
        {
            var result = await run();
            _expected[key] = result;
            return result;
        }
        catch (QueryFailedException ex)
        {
            throw new QuestionConfigurationException($"{question.Id}: the reference {reference.Name} failed on '{dataset.Name}': {ex.Message}");
        }
    }

    private static ComparisonResult Compare(Question question, Func<ComparisonResult> compare)
    {
        try
        {
            return compare();
        }
        catch (ContentException ex)
        {
            throw new QuestionConfigurationException($"{question.Id}: {ex.Message}");
        }
    }
}
