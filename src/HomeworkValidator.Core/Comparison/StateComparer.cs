using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using CompareOptions = HomeworkValidator.Core.Content.CompareOptions;

namespace HomeworkValidator.Core.Comparison;

/// <summary>
/// Compares the database after a student's DML script with the database after the reference solution:
/// the same tables must exist, and every table must hold the same rows (in any order, columns matched by name).
/// </summary>
public static class StateComparer
{
    /// <summary>Stands in for any non-NULL value in a presence-only column.</summary>
    private const string SetMarker = "(set)";

    /// <param name="fullRows">When above 0, the first table that differs also comes back whole (up to this many rows), both ways.</param>
    public static ComparisonResult Compare(DatabaseSnapshot expected, DatabaseSnapshot actual, CompareOptions options,
        int sampleSize = ResultComparer.DefaultSampleSize, int fullRows = 0)
    {
        var missingTables = expected.Tables.Keys.Where(t => !actual.Tables.ContainsKey(t)).Order().ToList();
        if (missingTables.Count > 0)
        {
            return new ComparisonResult(false,
                $"Expected {Names(missingTables)} to exist after your script, but {(missingTables.Count == 1 ? "it doesn't" : "they don't")}.")
                { Structural = true };
        }

        var extraTables = actual.Tables.Keys.Where(t => !expected.Tables.ContainsKey(t)).Order().ToList();
        if (extraTables.Count > 0)
        {
            return new ComparisonResult(false,
                $"Your script left {Names(extraTables)} in the database, which {(extraTables.Count == 1 ? "isn't" : "aren't")} expected.")
                { Structural = true };
        }

        var tableOptions = new CompareOptions(ColumnMatch.ByName, [], options.Decimals);
        var failures = new List<ComparisonResult>();
        foreach (var table in expected.Tables.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            var result = ResultComparer.Compare(
                PresenceOnly(expected.Tables[table], table, options),
                PresenceOnly(actual.Tables[table], table, options),
                tableOptions, sampleSize, table);
            if (!result.Passed)
                failures.Add(result);
        }

        if (failures.Count == 0)
            return new ComparisonResult(true, "Every table matches the expected result.");

        // Show the first table's differences; name any others so the student knows there's more.
        var first = failures[0];
        if (failures.Count > 1)
            first = first with { Message = $"{first.Message} Also different: {Names(failures.Skip(1).Select(f => f.Table!).ToList())}." };
        if (fullRows > 0)
        {
            first = first with
            {
                Expected = ResultComparer.Rows(expected.Tables[first.Table!], fullRows, options.Decimals),
                Actual = ResultComparer.Rows(actual.Tables[first.Table!], fullRows, options.Decimals),
            };
        }
        return first;
    }

    /// <summary>Replaces values in presence-only columns with a marker, so only "set or NULL" is compared.</summary>
    private static ResultTable PresenceOnly(ResultTable table, string name, CompareOptions options)
    {
        var columns = options.PresenceOnly
            .Select(entry => entry.Split('.'))
            .Where(parts => parts[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(parts => IndexOf(table.Columns, parts[1]))
            .Where(i => i >= 0)
            .ToList();
        if (columns.Count == 0)
            return table;

        var rows = table.Rows.Select(row =>
        {
            var copy = (object?[])row.Clone();
            foreach (var i in columns)
                copy[i] = copy[i] is null ? null : SetMarker;
            return copy;
        }).ToList();
        return table with { Rows = rows };
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string Names(IReadOnlyList<string> tables) => tables.Count switch
    {
        1 => $"table {tables[0]}",
        _ => $"tables {string.Join(", ", tables.Take(tables.Count - 1))} and {tables[^1]}",
    };
}
