using HomeworkValidator.Core.Comparison;

namespace HomeworkValidator.Core.Checking;

/// <summary>
/// One outcome and every dataset it happened on. Datasets whose results read the same (outcome, message and rows shown)
/// share a group, so the student sees each problem once instead of once per dataset.
/// </summary>
/// <param name="Datasets">The datasets' names, in the order they were checked.</param>
/// <param name="Result">
/// The first dataset's result, with the group's hint. The others say the same thing, though their whole results may differ.
/// </param>
/// <param name="AllDatasets">True when the group covers every dataset the answer was checked on.</param>
public sealed record DatasetGroup(IReadOnlyList<string> Datasets, DatasetResult Result, bool AllDatasets)
{
    public DatasetOutcome Outcome => Result.Outcome;
    public string Message => Result.Message;
    public ComparisonResult? Comparison => Result.Comparison;
    public string? Hint => Result.Hint;

    /// <summary>"Course data", "Course data and edge-case data", or "All datasets" when it's every one of several.</summary>
    public string Title => AllDatasets && Datasets.Count > 1 ? "All datasets" : DatasetNames.List(Datasets);

    public static IReadOnlyList<DatasetGroup> Group(IReadOnlyList<DatasetResult> results)
    {
        var groups = new List<List<DatasetResult>>();
        foreach (var result in results)
        {
            var group = groups.Find(g => Same(g[0], result));
            if (group is null)
                groups.Add([result]);
            else
                group.Add(result);
        }

        return groups
            .Select(g => new DatasetGroup(g.Select(r => r.Dataset).ToList(), g[0] with { Hint = SharedHint(g) }, g.Count == results.Count))
            .ToList();
    }

    /// <summary>
    /// A dataset's hint explains what that dataset catches that the others don't. It doesn't apply to a structural problem
    /// (wrong columns, a missing table, the design), which is the same on every dataset, or when a dataset without a hint
    /// (the course data) fails the same way. Otherwise each dataset's hint is kept.
    /// </summary>
    private static string? SharedHint(List<DatasetResult> group) =>
        group[0].Comparison is { Structural: true } || group.Any(r => r.Hint is null)
            ? null
            : string.Join(" ", group.Select(r => r.Hint).Distinct());

    /// <summary>True when the two results would show the student the same thing, apart from their hints.</summary>
    private static bool Same(DatasetResult a, DatasetResult b) =>
        a.Outcome == b.Outcome
        && a.Message == b.Message
        && a.Comparison?.Table == b.Comparison?.Table
        && Same(a.Comparison?.Missing, b.Comparison?.Missing)
        && Same(a.Comparison?.Unexpected, b.Comparison?.Unexpected);

    private static bool Same(RowSample? a, RowSample? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Total == b.Total
              && a.Columns.SequenceEqual(b.Columns)
              && a.Rows.Count == b.Rows.Count
              && a.Rows.Zip(b.Rows).All(pair => pair.First.SequenceEqual(pair.Second));
}

/// <summary>Dataset names as the student sees them.</summary>
public static class DatasetNames
{
    /// <summary>"Course data", "Edge-case data", "Shifted-ID data", or "{name} data" for any other dataset.</summary>
    public static string Title(string dataset)
    {
        var name = Name(dataset);
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>The name in the middle of a sentence: "course data", "edge-case data"...</summary>
    public static string Name(string dataset) => dataset switch
    {
        "base" => "course data",
        "edge" => "edge-case data",
        "shifted" => "shifted-ID data",
        var name => $"{name} data",
    };

    /// <summary>"Course data", "Course data and edge-case data", "Course data, edge-case data and shifted-ID data".</summary>
    public static string List(IReadOnlyList<string> datasets) => datasets.Count switch
    {
        0 => "",
        1 => Title(datasets[0]),
        _ => $"{string.Join(", ", datasets.SkipLast(1).Select((d, i) => i == 0 ? Title(d) : Name(d)))} and {Name(datasets[^1])}",
    };
}
