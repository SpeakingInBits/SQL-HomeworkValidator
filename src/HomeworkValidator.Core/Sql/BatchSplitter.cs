using System.Text;
using System.Text.RegularExpressions;

namespace HomeworkValidator.Core.Sql;

/// <summary>
/// Splits a trusted content script (schema or dataset) into batches on GO lines, the way SSMS and sqlcmd do.
/// Student SQL is split by ScriptDom instead; this is only for the validator's own scripts.
/// </summary>
public static partial class BatchSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();

        foreach (var line in script.ReplaceLineEndings("\n").Split('\n'))
        {
            var match = GoLine().Match(line);
            if (!match.Success)
            {
                current.Append(line).Append('\n');
                continue;
            }

            var repeat = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 1;
            for (var i = 0; i < repeat; i++)
                Add(batches, current);
            current.Clear();
        }

        Add(batches, current);
        return batches;
    }

    private static void Add(List<string> batches, StringBuilder batch)
    {
        var text = batch.ToString();
        if (!string.IsNullOrWhiteSpace(text))
            batches.Add(text);
    }

    [GeneratedRegex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
