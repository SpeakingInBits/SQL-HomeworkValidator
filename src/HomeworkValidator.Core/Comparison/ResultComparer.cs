using System.Globalization;
using CompareOptions = HomeworkValidator.Core.Content.CompareOptions;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Core.Comparison;

/// <summary>Rows to show the student, already formatted. <see cref="Total"/> may be larger than the rows shown.</summary>
public sealed record RowSample(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int Total);

public sealed record ComparisonResult(bool Passed, string Message, RowSample? Missing = null, RowSample? Unexpected = null)
{
    public static ComparisonResult Pass { get; } = new(true, "Matches the expected result.");

    /// <summary>For DML questions, the table the rows belong to; null for a query's result.</summary>
    public string? Table { get; init; }

    /// <summary>The whole expected result (or table), in its own order, when the checker was asked for it.</summary>
    public RowSample? Expected { get; init; }

    /// <summary>The whole result the student's SQL produced (or the table after their script), as they returned it.</summary>
    public RowSample? Actual { get; init; }
}

/// <summary>
/// Compares a student's result with the reference result. Rows are compared as a multiset; when the question
/// specifies sort keys, the sequence of keys must also match, which accepts any valid order among tied rows.
/// </summary>
public static class ResultComparer
{
    public const int DefaultSampleSize = 5;

    /// <param name="sampleSize">How many missing and unexpected rows to include for display.</param>
    /// <param name="table">When comparing a table after a DML script rather than a query's result: its name, for the messages.</param>
    public static ComparisonResult Compare(ResultTable expected, ResultTable actual, CompareOptions options,
        int sampleSize = DefaultSampleSize, string? table = null)
    {
        var words = new Wording(table);

        if (options.Columns == ColumnMatch.RowCount)
        {
            return expected.Rows.Count == actual.Rows.Count
                ? ComparisonResult.Pass
                : new ComparisonResult(false, words.RowCounts(expected.Rows.Count, actual.Rows.Count));
        }

        var (map, columnError) = AlignColumns(expected.Columns, actual.Columns, options.Columns, words);
        if (columnError is not null)
            return new ComparisonResult(false, columnError) { Table = table };

        var expectedRows = expected.Rows.Select(r => Format(r, options.Decimals)).ToList();
        var actualRows = actual.Rows.Select(r => Format(map!.Select(i => r[i]).ToArray(), options.Decimals)).ToList();

        var (missing, unexpected) = Difference(expectedRows, actualRows);
        if (missing.Count > 0 || unexpected.Count > 0)
        {
            var parts = new List<string>();
            if (expected.Rows.Count != actual.Rows.Count)
                parts.Add(words.RowCounts(expected.Rows.Count, actual.Rows.Count));
            if (missing.Count > 0)
                parts.Add(words.Missing(missing.Count));
            if (unexpected.Count > 0)
                parts.Add(words.Unexpected(unexpected.Count));

            return new ComparisonResult(false, string.Join(" ", parts),
                Missing: missing.Count > 0 ? Sample(expected.Columns, missing, sampleSize) : null,
                Unexpected: unexpected.Count > 0 ? Sample(expected.Columns, unexpected, sampleSize) : null) { Table = table };
        }

        return options.Ordered ? CompareOrder(expected.Columns, expectedRows, actualRows, options.Order) : ComparisonResult.Pass;
    }

    /// <summary>The same messages for a query's result ("your query returned...") and for a table ("VendorCopy has...").</summary>
    private sealed record Wording(string? Table)
    {
        public string RowCounts(int expected, int actual) => Table is null
            ? $"Expected {Rows(expected)}, but your query returned {Rows(actual)}."
            : $"Expected {Table} to have {Rows(expected)}, but it has {Rows(actual)}.";

        public string Missing(int count) => Table is null
            ? $"{Rows(count)} from the expected result {(count == 1 ? "is" : "are")} missing."
            : $"{Rows(count)} {(count == 1 ? "is" : "are")} missing from {Table}.";

        public string Unexpected(int count) => Table is null
            ? $"{Rows(count)} in your result {(count == 1 ? "isn't" : "aren't")} in the expected result."
            : $"{Rows(count)} in {Table} {(count == 1 ? "isn't" : "aren't")} expected.";

        public string TheirColumns => Table is null ? "Your columns" : $"{Table}'s columns";
        public string ExpectedColumns => Table is null ? "Expected columns" : $"Expected columns in {Table}";
    }

    private static ComparisonResult CompareOrder(
        IReadOnlyList<string> columns, List<string[]> expectedRows, List<string[]> actualRows, IReadOnlyList<OrderKey> order)
    {
        var keyIndexes = order.Select(k =>
        {
            var index = IndexOf(columns, k.Column);
            return index >= 0
                ? index
                : throw new ContentException($"compare.order names column '{k.Column}', which the reference solution doesn't return.");
        }).ToList();

        for (var row = 0; row < expectedRows.Count; row++)
        {
            for (var k = 0; k < order.Count; k++)
            {
                var expectedKey = Key(expectedRows[row][keyIndexes[k]], order[k]);
                var actualKey = Key(actualRows[row][keyIndexes[k]], order[k]);
                if (!string.Equals(expectedKey, actualKey, StringComparison.OrdinalIgnoreCase))
                {
                    var sortedBy = string.Join(", ", order.Select(o => o.Column));
                    return new ComparisonResult(false,
                        $"The rows are right, but they aren't in the right order (sorted by {sortedBy}). " +
                        $"Row {row + 1} should have {columns[keyIndexes[k]]} = {Display(expectedRows[row][keyIndexes[k]])}, " +
                        $"but yours has {Display(actualRows[row][keyIndexes[k]])}.");
                }
            }
        }

        return ComparisonResult.Pass;
    }

    private static string Key(string value, OrderKey key)
    {
        if (key.KeyPattern is { } pattern)
        {
            var match = pattern.Match(value);
            if (match.Success)
                value = match.Value;
        }

        return value.TrimEnd();
    }

    /// <summary>Returns a map from expected column index to actual column index, or an error message.</summary>
    private static (int[]? Map, string? Error) AlignColumns(IReadOnlyList<string> expected, IReadOnlyList<string> actual, ColumnMatch mode, Wording words)
    {
        if (mode == ColumnMatch.ByPosition)
        {
            return expected.Count == actual.Count
                ? (Enumerable.Range(0, expected.Count).ToArray(), null)
                : (null, $"Expected {Columns(expected.Count)}, but your query returned {Columns(actual.Count)}.");
        }

        var unnamed = actual.Count(string.IsNullOrWhiteSpace);
        if (unnamed > 0)
        {
            return (null,
                $"{Columns(unnamed)} in your result {(unnamed == 1 ? "has" : "have")} no name. " +
                $"Give calculated columns a name with AS. Expected columns: {List(expected)}.");
        }

        var duplicates = actual.GroupBy(c => c.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            return (null, $"Your result has more than one column named {List(duplicates)}. Expected columns: {List(expected)}.");

        var missing = expected.Where(c => IndexOf(actual, c) < 0).ToList();
        var extra = actual.Where(c => IndexOf(expected, c) < 0).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            var message = $"{words.ExpectedColumns}: {List(expected)}. {words.TheirColumns}: {List(actual)}.";
            if (missing.Count > 0)
                message += $" Missing: {List(missing)}.";
            if (extra.Count > 0)
                message += $" Not expected: {List(extra)}.";
            return (null, message);
        }

        return (expected.Select(c => IndexOf(actual, c)).ToArray(), null);
    }

    private static (List<string[]> Missing, List<string[]> Unexpected) Difference(List<string[]> expected, List<string[]> actual)
    {
        var remaining = new Dictionary<string, int>();
        foreach (var row in actual)
            remaining[RowKey(row)] = remaining.GetValueOrDefault(RowKey(row)) + 1;

        var missing = new List<string[]>();
        foreach (var row in expected)
        {
            var key = RowKey(row);
            if (remaining.GetValueOrDefault(key) > 0)
                remaining[key]--;
            else
                missing.Add(row);
        }

        var unexpected = new List<string[]>();
        foreach (var row in actual)
        {
            var key = RowKey(row);
            if (remaining.GetValueOrDefault(key) > 0)
            {
                remaining[key]--;
                unexpected.Add(row);
            }
        }

        return (missing, unexpected);
    }

    // NULL gets a value no formatted string can produce, so a NULL never equals the text "NULL".
    private const string NullKey = "\u0000";

    private static string RowKey(string[] row) => string.Join('\u001F', row);

    /// <summary>
    /// A whole result for display, in its own column and row order, formatted the way it's compared.
    /// Only the first <paramref name="maxRows"/> rows are included.
    /// </summary>
    public static RowSample Rows(ResultTable table, int maxRows, int? decimals = null) =>
        new(table.Columns,
            table.Rows.Take(maxRows).Select(r => (IReadOnlyList<string>)r.Select(v => Display(Format(v, decimals))).ToList()).ToList(),
            table.Rows.Count);

    private static RowSample Sample(IReadOnlyList<string> columns, List<string[]> rows, int sampleSize) =>
        new(columns, rows.Take(sampleSize).Select(r => (IReadOnlyList<string>)r.Select(Display).ToList()).ToList(), rows.Count);

    private static string Display(string cell) => cell == NullKey ? "NULL" : cell;

    private static string[] Format(object?[] row, int? decimals) => row.Select(v => Format(v, decimals)).ToArray();

    /// <summary>
    /// Formats a value so that equal values compare equal regardless of SQL type:
    /// 10, 10.00 and 10.0000 (int, decimal, money) are all "10"; a date and a midnight datetime match.
    /// </summary>
    public static string Format(object? value, int? decimals = null) => value switch
    {
        null => NullKey,
        string s => s.TrimEnd(),
        bool b => b ? "1" : "0",
        byte or short or int or long or decimal or float or double => FormatNumber(Convert.ToDecimal(value, CultureInfo.InvariantCulture), decimals),
        DateTime d when d.TimeOfDay == TimeSpan.Zero => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string FormatNumber(decimal number, int? decimals)
    {
        if (decimals is { } places)
            number = Math.Round(number, places, MidpointRounding.AwayFromZero);
        return number.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string List(IEnumerable<string> names) => string.Join(", ", names.Select(n => string.IsNullOrEmpty(n) ? "(no name)" : n));
    private static string Rows(int n) => n == 1 ? "1 row" : $"{n:N0} rows";
    private static string Columns(int n) => n == 1 ? "1 column" : $"{n} columns";
}
