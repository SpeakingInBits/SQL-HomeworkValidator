using HomeworkValidator.Core.Comparison;

namespace HomeworkValidator.Cli;

/// <summary>Console writing with colors and simple text tables.</summary>
public sealed class ConsoleOutput
{
    private const int MaxCellWidth = 40;

    public void Line(string text = "") => Console.WriteLine(text);

    public void Pass(string text) => Colored(ConsoleColor.Green, text);
    public void Fail(string text) => Colored(ConsoleColor.Red, text);
    public void Warn(string text) => Colored(ConsoleColor.Yellow, text);
    public void Dim(string text) => Colored(ConsoleColor.DarkGray, text);

    public void Table(RowSample sample, string indent)
    {
        var rows = sample.Rows.Select(r => r.Select(Truncate).ToList()).ToList();
        var widths = sample.Columns
            .Select((c, i) => Math.Max(Truncate(c).Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length)))
            .ToList();

        Console.WriteLine(indent + string.Join(" | ", sample.Columns.Select((c, i) => Truncate(c).PadRight(widths[i]))));
        Console.WriteLine(indent + string.Join("-+-", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            Console.WriteLine(indent + string.Join(" | ", row.Select((c, i) => c.PadRight(widths[i]))));
        if (sample.Total > sample.Rows.Count)
            Dim($"{indent}...and {sample.Total - sample.Rows.Count:N0} more");
    }

    private static string Truncate(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= MaxCellWidth ? value : value[..(MaxCellWidth - 3)] + "...";
    }

    private static void Colored(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
