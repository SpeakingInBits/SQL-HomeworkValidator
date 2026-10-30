using HomeworkValidator.Core.Execution;
using CompareOptions = HomeworkValidator.Core.Content.CompareOptions;

namespace HomeworkValidator.Core.Comparison;

/// <summary>
/// Compares the database design after a student's DDL script with the design after the reference solution.
/// Names of constraints and indexes don't matter; what they enforce does. Check constraints are only counted here,
/// since the same rule can be written many ways; the question's probes test what they actually allow.
/// </summary>
public static class SchemaComparer
{
    /// <summary>Most differences listed in one message; the rest are counted.</summary>
    public const int MaxDifferences = 8;

    public static ComparisonResult Compare(SchemaSnapshot expected, SchemaSnapshot actual, CompareOptions options)
    {
        var differences = new List<string>();

        foreach (var name in expected.Tables.Keys.Where(t => !actual.Tables.ContainsKey(t)).Order())
            differences.Add($"Table {name} is missing.");
        foreach (var name in actual.Tables.Keys.Where(t => !expected.Tables.ContainsKey(t)).Order())
            differences.Add($"Table {name} isn't expected.");

        foreach (var name in expected.Tables.Keys.Where(actual.Tables.ContainsKey).Order(StringComparer.OrdinalIgnoreCase))
            CompareTable(expected.Tables[name], actual.Tables[name], options, differences);

        if (differences.Count == 0)
            return new ComparisonResult(true, "The design matches the expected one.");

        var shown = differences.Take(MaxDifferences).ToList();
        if (differences.Count > MaxDifferences)
            shown.Add($"...and {differences.Count - MaxDifferences} more.");
        return new ComparisonResult(false, string.Join(Environment.NewLine, shown)) { Structural = true };
    }

    private static void CompareTable(TableSchema expected, TableSchema actual, CompareOptions options, List<string> differences)
    {
        var table = expected.Name;

        foreach (var column in expected.Columns)
        {
            var other = actual.Columns.FirstOrDefault(c => Same(c.Name, column.Name));
            if (other is null)
            {
                differences.Add($"{table}: column {column.Name} is missing.");
                continue;
            }

            var expectedType = options.IgnoreLengths ? column.BaseType : column.Type;
            var actualType = options.IgnoreLengths ? other.BaseType : other.Type;
            if (!Same(expectedType, actualType))
                differences.Add($"{table}.{column.Name} should be {expectedType}, but it's {actualType}.");
            if (!options.IgnoreNullability && column.Nullable != other.Nullable)
                differences.Add($"{table}.{column.Name} should {(column.Nullable ? "allow" : "not allow")} NULLs.");
            if (column.Identity != other.Identity)
                differences.Add($"{table}.{column.Name} should {(column.Identity ? "be" : "not be")} an identity column.");
            if (column.HasDefault != other.HasDefault)
                differences.Add($"{table}.{column.Name} should {(column.HasDefault ? "have" : "not have")} a default value.");
        }

        foreach (var column in actual.Columns.Where(c => !expected.Columns.Any(e => Same(e.Name, c.Name))))
            differences.Add($"{table}: column {column.Name} isn't expected.");

        ComparePrimaryKey(table, expected, actual, differences);

        // Unique constraints and unique indexes enforce the same thing, so they're compared together, ignoring column order.
        CompareSets(
            Keys(expected.Indexes.Where(i => i.Unique && !i.PrimaryKey).Select(i => List(i.Columns.Order(StringComparer.OrdinalIgnoreCase)))),
            Keys(actual.Indexes.Where(i => i.Unique && !i.PrimaryKey).Select(i => List(i.Columns.Order(StringComparer.OrdinalIgnoreCase)))),
            key => $"{table} needs a unique constraint on ({key}).",
            key => $"{table} has a unique constraint on ({key}) that isn't expected.",
            differences);

        CompareSets(
            Keys(expected.Indexes.Where(i => !i.Unique && !i.PrimaryKey).Select(IndexKey)),
            Keys(actual.Indexes.Where(i => !i.Unique && !i.PrimaryKey).Select(IndexKey)),
            key => $"{table} needs a {key}.",
            key => $"{table} has a {key} that isn't expected.",
            differences);

        CompareSets(
            Keys(expected.ForeignKeys.Select(ForeignKeyKey)),
            Keys(actual.ForeignKeys.Select(ForeignKeyKey)),
            key => $"{table} needs a foreign key {key}.",
            key => $"{table} has a foreign key {key} that isn't expected.",
            differences);

        if (expected.CheckConstraints != actual.CheckConstraints)
        {
            differences.Add($"{table} should have {Count(expected.CheckConstraints, "check constraint")}, " +
                            $"but it has {actual.CheckConstraints}.");
        }
    }

    private static void ComparePrimaryKey(string table, TableSchema expected, TableSchema actual, List<string> differences)
    {
        var expectedKey = expected.Indexes.FirstOrDefault(i => i.PrimaryKey);
        var actualKey = actual.Indexes.FirstOrDefault(i => i.PrimaryKey);
        switch (expectedKey, actualKey)
        {
            case (null, null):
                break;
            case (null, not null):
                differences.Add($"{table} shouldn't have a primary key, but it has one on ({List(actualKey.Columns)}).");
                break;
            case (not null, null):
                differences.Add($"{table} should have a primary key on ({List(expectedKey.Columns)}).");
                break;
            default:
                if (!Same(List(expectedKey!.Columns), List(actualKey!.Columns)))
                    differences.Add($"{table}'s primary key should be on ({List(expectedKey.Columns)}), not ({List(actualKey.Columns)}).");
                break;
        }
    }

    private static string IndexKey(IndexSchema index) =>
        $"{(index.Clustered ? "clustered" : "nonclustered")} index on ({List(index.Columns)})";

    private static string ForeignKeyKey(ForeignKeySchema key)
    {
        var text = $"({List(key.Columns)}) referencing {key.ReferencedTable}({List(key.ReferencedColumns)})";
        if (key.OnDelete != "NO_ACTION")
            text += $" ON DELETE {key.OnDelete.Replace('_', ' ')}";
        if (key.OnUpdate != "NO_ACTION")
            text += $" ON UPDATE {key.OnUpdate.Replace('_', ' ')}";
        return text;
    }

    private static List<string> Keys(IEnumerable<string> keys) => keys.ToList();

    /// <summary>Compares two lists of descriptions as multisets, case-insensitively.</summary>
    private static void CompareSets(
        List<string> expected, List<string> actual, Func<string, string> missing, Func<string, string> extra, List<string> differences)
    {
        var remaining = actual.ToList();
        foreach (var key in expected)
        {
            var match = remaining.FindIndex(k => Same(k, key));
            if (match >= 0)
                remaining.RemoveAt(match);
            else
                differences.Add(missing(key));
        }

        differences.AddRange(remaining.Select(extra));
    }

    private static string List(IEnumerable<string> columns) => string.Join(", ", columns);
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>Compares what the question's probes did against the expected design and against the student's.</summary>
public static class ProbeComparer
{
    public static ComparisonResult Compare(IReadOnlyList<ProbeResult> expected, IReadOnlyList<ProbeResult> actual, int sampleSize)
    {
        for (var i = 0; i < expected.Count; i++)
        {
            var want = expected[i];
            var got = i < actual.Count ? actual[i] : new ProbeResult(want.Name, "It couldn't run, because an earlier test left the transaction unusable.", null);

            if (want.Succeeded && !got.Succeeded)
                return new ComparisonResult(false, $"{want.Name}: this should work, but with your design it fails: {got.Error}");
            if (!want.Succeeded && got.Succeeded)
                return new ComparisonResult(false, $"{want.Name}: this should fail (with the expected design: {want.Error}), but with your design it works.");

            if (want.Result is not null && got.Result is not null)
            {
                var result = ResultComparer.Compare(want.Result, got.Result, Content.CompareOptions.Default, sampleSize);
                if (!result.Passed)
                    return result with { Message = $"{want.Name}: {result.Message}" };
            }
        }

        return new ComparisonResult(true, "Every test of the design behaves as expected.");
    }
}
