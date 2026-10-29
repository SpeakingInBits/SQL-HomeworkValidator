using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomeworkValidator.Core.Content;

/// <summary>
/// Loads course databases and assignments from a content folder:
/// <code>
/// databases/&lt;Name&gt;/schema.sql
/// databases/&lt;Name&gt;/datasets/&lt;dataset&gt;.sql
/// assignments/&lt;id&gt;/assignment.yaml
/// assignments/&lt;id&gt;/q&lt;n&gt;.yaml, q&lt;n&gt;.sql, q&lt;n&gt;.mutants/*.sql
/// </code>
/// </summary>
public sealed partial class ContentLibrary
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public required string Root { get; init; }
    public required IReadOnlyList<CourseDatabase> Databases { get; init; }
    public required IReadOnlyList<Assignment> Assignments { get; init; }

    public IEnumerable<Question> Questions => Assignments.SelectMany(a => a.Questions);

    public CourseDatabase GetDatabase(string name) =>
        Databases.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ContentException($"There is no course database named '{name}'.");

    public Question GetQuestion(string id) =>
        Questions.FirstOrDefault(q => q.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ContentException($"There is no question '{id}'. Run 'hv list' to see question IDs.");

    /// <summary>Questions matching an assignment ID, a question ID, or everything when <paramref name="filter"/> is null.</summary>
    public IReadOnlyList<Question> FindQuestions(string? filter)
    {
        if (filter is null)
            return Questions.ToList();

        var assignment = Assignments.FirstOrDefault(a => a.Id.Equals(filter, StringComparison.OrdinalIgnoreCase));
        return assignment is not null ? assignment.Questions : [GetQuestion(filter)];
    }

    public static ContentLibrary Load(string root)
    {
        if (!Directory.Exists(root))
            throw new ContentException($"Content folder not found: {root}");

        var databases = LoadDatabases(Path.Combine(root, "databases"));
        var assignments = LoadAssignments(Path.Combine(root, "assignments"), databases);
        return new ContentLibrary { Root = root, Databases = databases, Assignments = assignments };
    }

    /// <summary>
    /// Finds the content folder: next to the executable when published, otherwise by walking up
    /// from <paramref name="start"/> to the repository's <c>content</c> folder.
    /// </summary>
    public static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "content");
            if (Directory.Exists(Path.Combine(candidate, "databases")))
                return candidate;
        }

        throw new ContentException($"Couldn't find a content folder above {start}.");
    }

    private static List<CourseDatabase> LoadDatabases(string dir)
    {
        var result = new List<CourseDatabase>();
        foreach (var dbDir in Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dbDir);
            if (!SafeName().IsMatch(name))
                throw new ContentException($"Database folder name '{name}' may only contain letters, digits and underscores.");

            var schemaPath = Path.Combine(dbDir, "schema.sql");
            if (!File.Exists(schemaPath))
                throw new ContentException($"{schemaPath} is missing.");

            var datasets = Directory.GetFiles(Path.Combine(dbDir, "datasets"), "*.sql")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => new Dataset(Path.GetFileNameWithoutExtension(p), File.ReadAllText(p)))
                .ToList();

            if (datasets.Count == 0)
                throw new ContentException($"Database '{name}' has no datasets.");
            foreach (var ds in datasets.Where(ds => !SafeName().IsMatch(ds.Name)))
                throw new ContentException($"Dataset name '{ds.Name}' in '{name}' may only contain letters, digits and underscores.");

            // base comes first: it's the dataset students know.
            datasets = datasets.OrderBy(d => d.Name.Equals("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            result.Add(new CourseDatabase(name, File.ReadAllText(schemaPath), datasets));
        }

        return result;
    }

    private static List<Assignment> LoadAssignments(string dir, List<CourseDatabase> databases)
    {
        var result = new List<Assignment>();
        if (!Directory.Exists(dir))
            return result;

        foreach (var assignmentDir in Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(assignmentDir);
            var manifestPath = Path.Combine(assignmentDir, "assignment.yaml");
            var manifest = Deserialize<AssignmentYaml>(manifestPath);
            var database = databases.FirstOrDefault(d => d.Name.Equals(manifest.Database, StringComparison.OrdinalIgnoreCase))
                ?? throw new ContentException($"{manifestPath}: unknown database '{manifest.Database}'.");

            var questions = Directory.GetFiles(assignmentDir, "q*.yaml")
                .Select(path => (path, number: ParseQuestionNumber(path)))
                .OrderBy(x => x.number)
                .Select(x => LoadQuestion(x.path, x.number, id, database))
                .ToList();

            result.Add(new Assignment(id, Required(manifest.Title, manifestPath, "title"), database.Name, questions));
        }

        return result;
    }

    private static Question LoadQuestion(string path, int number, string assignmentId, CourseDatabase database)
    {
        var yaml = Deserialize<QuestionYaml>(path);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

        var solutionPath = stem + ".sql";
        if (!File.Exists(solutionPath))
            throw new ContentException($"{path}: reference solution {Path.GetFileName(solutionPath)} is missing.");

        var mutantsDir = stem + ".mutants";
        var mutants = Directory.Exists(mutantsDir)
            ? Directory.GetFiles(mutantsDir, "*.sql")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => new Mutant(Path.GetFileNameWithoutExtension(p), File.ReadAllText(p)))
                .ToList()
            : [];

        var datasets = yaml.Datasets ?? database.Datasets.Select(d => d.Name).ToList();
        foreach (var ds in datasets)
            database.GetDataset(ds);

        var hints = yaml.Hints ?? [];
        foreach (var key in hints.Keys.Where(k => !datasets.Contains(k, StringComparer.OrdinalIgnoreCase)))
            throw new ContentException($"{path}: hint for '{key}', which isn't one of this question's datasets.");

        return new Question(
            Id: $"{assignmentId}/q{number}",
            AssignmentId: assignmentId,
            Number: number,
            Title: Required(yaml.Title, path, "title"),
            Prompt: Required(yaml.Prompt, path, "prompt").Trim(),
            Kind: ParseEnum<QuestionKind>(yaml.Kind ?? "select", path, "kind"),
            Database: database.Name,
            Datasets: datasets,
            Compare: ParseCompare(yaml.Compare, path),
            Rules: new QuestionRules(
                ParseFeatures(yaml.Rules?.Require, path),
                ParseFeatures(yaml.Rules?.Forbid, path)),
            Hints: new Dictionary<string, string>(hints, StringComparer.OrdinalIgnoreCase),
            SolutionSql: File.ReadAllText(solutionPath),
            Mutants: mutants);
    }

    private static CompareOptions ParseCompare(CompareYaml? yaml, string path)
    {
        if (yaml is null)
            return CompareOptions.Default;

        var columns = yaml.Columns switch
        {
            null or "by-name" => ColumnMatch.ByName,
            "by-position" => ColumnMatch.ByPosition,
            "row-count" => ColumnMatch.RowCount,
            var other => throw new ContentException($"{path}: compare.columns must be by-name, by-position or row-count, not '{other}'."),
        };

        var order = new List<OrderKey>();
        foreach (var item in yaml.Order ?? [])
        {
            // Either a plain column name or { column: X, keyPattern: regex }.
            switch (item)
            {
                case string column:
                    order.Add(new OrderKey(column, null));
                    break;
                case IDictionary<object, object> map:
                    var col = map.TryGetValue("column", out var c) ? c as string : null;
                    var pattern = map.TryGetValue("keyPattern", out var p) ? p as string : null;
                    if (col is null || map.Keys.Any(k => k is not ("column" or "keyPattern")))
                        throw new ContentException($"{path}: each compare.order item is a column name or {{ column, keyPattern }}.");
                    order.Add(new OrderKey(col, pattern is null ? null : new Regex(pattern, RegexOptions.CultureInvariant)));
                    break;
                default:
                    throw new ContentException($"{path}: each compare.order item is a column name or {{ column, keyPattern }}.");
            }
        }

        if (columns == ColumnMatch.RowCount && order.Count > 0)
            throw new ContentException($"{path}: compare.order can't be used with columns: row-count.");

        return new CompareOptions(columns, order, yaml.Decimals);
    }

    private static HashSet<SqlFeature> ParseFeatures(List<string>? names, string path) =>
        (names ?? []).Select(n => ParseEnum<SqlFeature>(n, path, "rules")).ToHashSet();

    private static T ParseEnum<T>(string value, string path, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value.Replace("-", ""), ignoreCase: true, out var result)
            ? result
            : throw new ContentException($"{path}: '{value}' isn't a valid {field} value. Valid values: {string.Join(", ", Enum.GetNames<T>())}.");

    private static int ParseQuestionNumber(string path)
    {
        var match = QuestionFileName().Match(Path.GetFileNameWithoutExtension(path));
        return match.Success
            ? int.Parse(match.Groups[1].Value)
            : throw new ContentException($"{path}: question files are named q<number>.yaml.");
    }

    private static string Required(string? value, string path, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ContentException($"{path}: '{field}' is required.") : value;

    private static T Deserialize<T>(string path)
    {
        if (!File.Exists(path))
            throw new ContentException($"{path} is missing.");
        try
        {
            return Yaml.Deserialize<T>(File.ReadAllText(path))
                ?? throw new ContentException($"{path} is empty.");
        }
        catch (YamlException ex)
        {
            throw new ContentException($"{path}: {ex.Message}");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^q(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionFileName();

    private sealed class AssignmentYaml
    {
        public string? Title { get; set; }
        public string Database { get; set; } = "";
    }

    private sealed class QuestionYaml
    {
        public string? Title { get; set; }
        public string? Prompt { get; set; }
        public string? Kind { get; set; }
        public List<string>? Datasets { get; set; }
        public CompareYaml? Compare { get; set; }
        public RulesYaml? Rules { get; set; }
        public Dictionary<string, string>? Hints { get; set; }
    }

    private sealed class CompareYaml
    {
        public string? Columns { get; set; }
        public List<object>? Order { get; set; }
        public int? Decimals { get; set; }
    }

    private sealed class RulesYaml
    {
        public List<string>? Require { get; set; }
        public List<string>? Forbid { get; set; }
    }
}
