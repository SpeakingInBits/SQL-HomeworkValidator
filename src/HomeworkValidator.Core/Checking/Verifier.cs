using HomeworkValidator.Core.Content;

namespace HomeworkValidator.Core.Checking;

public sealed record MutantResult(string Name, bool Caught, string Reason);

public sealed record QuestionVerification(Question Question, IReadOnlyList<string> Problems, IReadOnlyList<MutantResult> Mutants)
{
    public bool Passed => Problems.Count == 0 && Mutants.All(m => m.Caught);
}

/// <summary>
/// Instructor check for content: every reference solution must run, pass its own rules and match itself,
/// and every mutant (known-wrong answer) must fail. A mutant that passes means the datasets can't tell it apart.
/// </summary>
public sealed class Verifier(Checker checker)
{
    public async Task<QuestionVerification> VerifyAsync(Question question, CancellationToken ct = default)
    {
        var problems = new List<string>();
        try
        {
            // The solution and every alternative must run cleanly and follow the question's rules.
            foreach (var reference in question.Alternatives.Prepend(new NamedScript("solution", question.SolutionSql)))
            {
                var label = reference.Name == "solution" ? "Reference solution" : $"Alternative '{reference.Name}'";
                var self = await checker.CheckAsync(question, reference.Sql, ct);
                problems.AddRange(self.Errors.Select(e => $"{label}: {e}"));
                problems.AddRange(self.RuleViolations.Select(v => $"{label} breaks the question's rule: {v}"));
                problems.AddRange(self.Groups
                    .Where(g => g.Outcome != DatasetOutcome.Passed)
                    .Select(g => $"{label} doesn't pass. {g.Title}: {g.Message}"));
            }
        }
        catch (QuestionConfigurationException ex)
        {
            return new QuestionVerification(question, [ex.Message], []);
        }

        var mutants = new List<MutantResult>();
        foreach (var mutant in question.Mutants)
        {
            var report = await checker.CheckAsync(question, mutant.Sql, ct);
            mutants.Add(report.Passed
                ? new MutantResult(mutant.Name, false, "Passed every check.")
                : new MutantResult(mutant.Name, true, FirstReason(report)));
        }

        return new QuestionVerification(question, problems, mutants);
    }

    private static string FirstReason(CheckReport report)
    {
        if (report.Errors.Count > 0)
            return report.Errors[0];
        if (report.RuleViolations.Count > 0)
            return report.RuleViolations[0];
        var failed = report.Groups.First(g => g.Outcome != DatasetOutcome.Passed);
        return $"{failed.Title}: {failed.Message}";
    }
}
