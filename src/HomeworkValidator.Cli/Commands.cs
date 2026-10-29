using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Cli;

public static class Commands
{
    public static int List(ContentLibrary content, ConsoleOutput output)
    {
        foreach (var assignment in content.Assignments)
        {
            output.Line($"{assignment.Id}  {assignment.Title} ({assignment.Database})");
            foreach (var question in assignment.Questions)
                output.Line($"  {question.Id,-14} {question.Title}");
            output.Line();
        }

        return 0;
    }

    public static int Show(ContentLibrary content, string questionId, ConsoleOutput output)
    {
        var question = content.GetQuestion(questionId);
        output.Line($"{question.Id}: {question.Title}");
        output.Dim($"Database: {question.Database}");
        output.Line();
        output.Line(question.Prompt);
        return 0;
    }

    public static async Task<int> SetupAsync(ContentLibrary content, DatabaseManager databases, ConsoleOutput output)
    {
        foreach (var status in await databases.GetStatusAsync(content))
        {
            if (status.State == DatabaseState.Current)
            {
                output.Dim($"{status.Name}: up to date");
                continue;
            }

            Console.Write($"{status.Name}: {(status.State == DatabaseState.Missing ? "creating" : "updating")}... ");
            await databases.EnsureAsync(status.Database, status.Dataset);
            output.Pass("done");
        }

        return 0;
    }

    public static async Task<int> CheckAsync(ContentLibrary content, Checker checker, string questionId, string file, ConsoleOutput output)
    {
        var question = content.GetQuestion(questionId);
        if (!File.Exists(file))
            throw new UsageException($"File not found: {file}");

        var report = await checker.CheckAsync(question, await File.ReadAllTextAsync(file));
        PrintReport(report, output);
        return report.Passed ? 0 : 1;
    }

    public static async Task<int> VerifyAsync(
        ContentLibrary content, Verifier verifier, string? filter, ConsoleOutput output)
    {
        var questions = content.FindQuestions(filter);
        var failed = 0;
        foreach (var question in questions)
        {
            var result = await verifier.VerifyAsync(question);
            if (result.Passed)
            {
                output.Pass($"PASS  {question.Id}  ({result.Mutants.Count} mutant{(result.Mutants.Count == 1 ? "" : "s")} caught)");
            }
            else
            {
                failed++;
                output.Fail($"FAIL  {question.Id}");
            }

            foreach (var problem in result.Problems)
                output.Fail($"      {problem}");
            foreach (var mutant in result.Mutants)
            {
                if (mutant.Caught)
                    output.Dim($"      caught  {mutant.Name}: {mutant.Reason}");
                else
                    output.Fail($"      MISSED  {mutant.Name}: {mutant.Reason} The datasets can't tell this wrong answer apart.");
            }
        }

        output.Line();
        if (failed == 0)
            output.Pass($"All {questions.Count} questions verified.");
        else
            output.Fail($"{failed} of {questions.Count} questions have problems.");
        return failed == 0 ? 0 : 1;
    }

    public static async Task<int> CleanAsync(DatabaseManager databases, ConsoleOutput output)
    {
        var dropped = await databases.DropAllAsync();
        foreach (var name in dropped)
            output.Line($"Dropped {name}");
        output.Line(dropped.Count == 0 ? "No validator databases found." : $"Dropped {dropped.Count} database(s).");
        return 0;
    }

    private static void PrintReport(CheckReport report, ConsoleOutput output)
    {
        output.Line($"{report.Question.Id}: {report.Question.Title}");
        foreach (var note in report.Notes)
            output.Dim($"  Note: {note}");

        foreach (var error in report.Errors)
            output.Fail($"  {error}");
        foreach (var violation in report.RuleViolations)
            output.Fail($"  Rule: {violation}");

        foreach (var dataset in report.Datasets)
        {
            switch (dataset.Outcome)
            {
                case DatasetOutcome.Passed:
                    output.Pass($"  PASS  {dataset.Dataset} data");
                    break;
                case DatasetOutcome.Error:
                    output.Fail($"  ERROR {dataset.Dataset} data: your query failed to run.");
                    foreach (var line in dataset.Message.Split(Environment.NewLine))
                        output.Line($"        {line}");
                    break;
                default:
                    output.Fail($"  FAIL  {dataset.Dataset} data");
                    output.Line($"        {dataset.Message}");
                    if (dataset.Comparison?.Missing is { } missing)
                    {
                        output.Line("        Missing rows:");
                        output.Table(missing, "          ");
                    }
                    if (dataset.Comparison?.Unexpected is { } unexpected)
                    {
                        output.Line("        Rows that shouldn't be there:");
                        output.Table(unexpected, "          ");
                    }
                    if (dataset.Hint is not null)
                        output.Warn($"        Hint: {dataset.Hint}");
                    break;
            }
        }

        output.Line();
        if (report.Passed)
            output.Pass("Correct!");
        else
            output.Fail("Not quite. See the details above.");
    }
}
