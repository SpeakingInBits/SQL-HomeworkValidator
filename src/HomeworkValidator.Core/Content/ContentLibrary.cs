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
/// assignments/&lt;id&gt;/q&lt;n&gt;.yaml, q&lt;n&gt;.sql, q&lt;n&gt;.mutants/*.sql, q&lt;n&gt;.alternatives/*.sql, q&lt;n&gt;.setup.sql
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
    /// Finds the content folder: next to the executable when published, in <c>Contents/Resources</c> inside a macOS app bundle,
    /// otherwise by walking up from <paramref name="start"/> to the repository's <c>content</c> folder.
    /// </summary>
    public static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "content"), Path.Combine(dir.FullName, "Resources", "content") })
            {
                if (Directory.Exists(Path.Combine(candidate, "databases")))
                    return candidate;
            }
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

            var datasetsDir = Path.Combine(dbDir, "datasets");
            var hintsPath = Path.Combine(datasetsDir, "hints.yaml");
            var hints = File.Exists(hintsPath)
                ? new Dictionary<string, string>(Deserialize<Dictionary<string, string>>(hintsPath), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var datasets = Directory.GetFiles(datasetsDir, "*.sql")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
                .Select(f => new Dataset(f.Name, ReadScript(f.Path, []), hints.GetValueOrDefault(f.Name)))
                .ToList();

            foreach (var key in hints.Keys.Where(k => datasets.All(d => !d.Name.Equals(k, StringComparison.OrdinalIgnoreCase))))
                throw new ContentException($"{hintsPath}: hint for '{key}', which isn't a dataset.");

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

    /// <summary>
    /// Reads a dataset script, replacing sqlcmd-style <c>:r file.sql</c> lines with that file's contents
    /// (relative to the including file), so the edge dataset can build on base without copying it.
    /// </summary>
    private static string ReadScript(string path, HashSet<string> including)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new ContentException($"{path} is missing.");
        if (!including.Add(fullPath))
            throw new ContentException($"{path} includes itself through ':r'.");

        var text = IncludeLine().Replace(File.ReadAllText(fullPath), match =>
        {
            var file = match.Groups[1].Value.Trim().Trim('"');
            return ReadScript(Path.Combine(Path.GetDirectoryName(fullPath)!, file), including).TrimEnd() + Environment.NewLine + "GO";
        });

        including.Remove(fullPath);
        return text;
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
                .Select(x => LoadQuestion(x.path, x.number, id, database, databases))
                .ToList();

            var assignment = new Assignment(id, Required(manifest.Title, manifestPath, "title"), database.Name, questions);
            CheckAfterReferences(assignment, assignmentDir);
            result.Add(assignment);
        }

        return result;
    }

    private static Question LoadQuestion(string path, int number, string assignmentId, CourseDatabase assignmentDatabase, List<CourseDatabase> databases)
    {
        var yaml = Deserialize<QuestionYaml>(path);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

        var solutionPath = stem + ".sql";
        if (!File.Exists(solutionPath))
            throw new ContentException($"{path}: reference solution {Path.GetFileName(solutionPath)} is missing.");

        var kind = ParseEnum<QuestionKind>(yaml.Kind ?? "select", path, "kind");
        if (kind == QuestionKind.Select && (yaml.After is not null || File.Exists(stem + ".setup.sql")))
            throw new ContentException($"{path}: 'after' and setup scripts are only for kind: dml and ddl questions.");
        if (kind != QuestionKind.Ddl && yaml.Probes is not null)
            throw new ContentException($"{path}: probes are only for kind: ddl questions.");

        // A question can use a different database than the rest of its assignment (e.g. one AP exercise in a Membership homework).
        var database = yaml.Database is null
            ? assignmentDatabase
            : databases.FirstOrDefault(d => d.Name.Equals(yaml.Database, StringComparison.OrdinalIgnoreCase))
              ?? throw new ContentException($"{path}: unknown database '{yaml.Database}'.");

        var probes = (yaml.Probes ?? []).Select((p, i) => new NamedScript(
            Required(p.Name, path, $"probes[{i}].name"),
            Required(p.Sql, path, $"probes[{i}].sql"))).ToList();

        var after = (yaml.After ?? []).Select(name =>
        {
            var match = QuestionFileName().Match(name);
            if (!match.Success || int.Parse(match.Groups[1].Value) >= number)
                throw new ContentException($"{path}: 'after' lists '{name}'; it must name an earlier question in this assignment, like q1.");
            return $"{assignmentId}/q{int.Parse(match.Groups[1].Value)}";
        }).ToList();

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
            Kind: kind,
            Database: database.Name,
            Datasets: datasets,
            Compare: ParseCompare(yaml.Compare, kind, path),
            Rules: new QuestionRules(
                ParseFeatures(yaml.Rules?.Require, path),
                ParseFeatures(yaml.Rules?.Forbid, path)),
            Hints: new Dictionary<string, string>(hints, StringComparer.OrdinalIgnoreCase),
            SolutionSql: File.ReadAllText(solutionPath),
            Mutants: ReadScripts(stem + ".mutants"))
        {
            Note = yaml.Note?.Trim(),
            Alternatives = ReadScripts(stem + ".alternatives"),
            After = after,
            SetupSql = File.Exists(stem + ".setup.sql") ? File.ReadAllText(stem + ".setup.sql") : null,
            Probes = probes,
        };
    }

    /// <summary>Every .sql file in a folder such as q5.mutants/ or q5.alternatives/, named by file name.</summary>
    private static List<NamedScript> ReadScripts(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.sql")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => new NamedScript(Path.GetFileNameWithoutExtension(p), File.ReadAllText(p)))
                .ToList()
            : [];

    /// <summary>
    /// A question's 'after' list must name DML or DDL questions on the same database, since their solutions set up
    /// its starting state.
    /// </summary>
    private static void CheckAfterReferences(Assignment assignment, string dir)
    {
        foreach (var question in assignment.Questions)
        {
            foreach (var id in question.After)
            {
                var earlier = assignment.Questions.FirstOrDefault(q => q.Id == id)
                    ?? throw new ContentException($"{dir}: {question.Id} runs after {id}, which doesn't exist.");
                if (!earlier.IsScript)
                    throw new ContentException($"{dir}: {question.Id} runs after {id}, which isn't a kind: dml or ddl question.");
                if (!earlier.Database.Equals(question.Database, StringComparison.OrdinalIgnoreCase))
                    throw new ContentException($"{dir}: {question.Id} runs after {id}, which uses a different database.");
            }
        }
    }

    private static CompareOptions ParseCompare(CompareYaml? yaml, QuestionKind kind, string path)
    {
        if (yaml is null)
            return CompareOptions.Default;

        if (kind != QuestionKind.Select && (yaml.Columns is not null || yaml.Order is not null))
            throw new ContentException($"{path}: DML and DDL questions compare whole tables, so compare.columns and compare.order don't apply.");
        if (kind == QuestionKind.Select && yaml.PresenceOnly is not null)
            throw new ContentException($"{path}: compare.presenceOnly is only for kind: dml and ddl questions.");
        if (kind != QuestionKind.Ddl && (yaml.IgnoreLengths is not null || yaml.IgnoreNullability is not null))
            throw new ContentException($"{path}: compare.ignoreLengths and compare.ignoreNullability are only for kind: ddl questions.");
        foreach (var name in (yaml.PresenceOnly ?? []).Where(n => n.Split('.').Length != 2))
            throw new ContentException($"{path}: compare.presenceOnly entries look like Table.Column, not '{name}'.");

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

        return new CompareOptions(columns, order, yaml.Decimals)
        {
            PresenceOnly = yaml.PresenceOnly ?? [],
            IgnoreLengths = yaml.IgnoreLengths ?? false,
            IgnoreNullability = yaml.IgnoreNullability ?? false,
        };
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

    [GeneratedRegex(@"^[ \t]*:r[ \t]+(.+?)[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex IncludeLine();

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
        public string? Note { get; set; }
        public List<string>? After { get; set; }
        public string? Database { get; set; }
        public List<ProbeYaml>? Probes { get; set; }
    }

    private sealed class CompareYaml
    {
        public string? Columns { get; set; }
        public List<object>? Order { get; set; }
        public int? Decimals { get; set; }
        public List<string>? PresenceOnly { get; set; }
        public bool? IgnoreLengths { get; set; }
        public bool? IgnoreNullability { get; set; }
    }

    private sealed class ProbeYaml
    {
        public string? Name { get; set; }
        public string? Sql { get; set; }
    }

    private sealed class RulesYaml
    {
        public List<string>? Require { get; set; }
        public List<string>? Forbid { get; set; }
    }
}
