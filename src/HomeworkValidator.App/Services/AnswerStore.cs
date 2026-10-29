using System.Text.Json;
using System.Text.Json.Serialization;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.Services;

[JsonConverter(typeof(JsonStringEnumConverter<QuestionStatus>))]
public enum QuestionStatus
{
    NotChecked,
    Passed,
    Failed,
}

/// <summary>
/// Keeps each question's answer and last result on disk, so students don't lose work between sessions:
/// answers/&lt;assignment&gt;/q&lt;n&gt;.sql and status.json in the app data folder.
/// </summary>
public sealed class AnswerStore
{
    private readonly string _root;
    private readonly Dictionary<string, QuestionStatus> _statuses;

    public AnswerStore(string? root = null)
    {
        _root = root ?? Path.Combine(AppDataFolder.Root, "answers");
        _statuses = LoadStatuses();
    }

    private string StatusPath => Path.Combine(_root, "status.json");

    public string LoadAnswer(Question question)
    {
        var path = AnswerPath(question);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveAnswer(Question question, string sql)
    {
        var path = AnswerPath(question);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sql);
    }

    public QuestionStatus GetStatus(Question question) => _statuses.GetValueOrDefault(question.Id, QuestionStatus.NotChecked);

    public void SetStatus(Question question, QuestionStatus status)
    {
        _statuses[question.Id] = status;
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatusPath, JsonSerializer.Serialize(_statuses, AppJsonContext.Default.DictionaryStringQuestionStatus));
    }

    private string AnswerPath(Question question) => Path.Combine(_root, question.AssignmentId, $"q{question.Number}.sql");

    private Dictionary<string, QuestionStatus> LoadStatuses()
    {
        try
        {
            if (File.Exists(StatusPath))
            {
                var saved = JsonSerializer.Deserialize(File.ReadAllText(StatusPath), AppJsonContext.Default.DictionaryStringQuestionStatus);
                if (saved is not null)
                    return new Dictionary<string, QuestionStatus>(saved, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Statuses are a convenience; start fresh if the file is damaged.
        }

        return new Dictionary<string, QuestionStatus>(StringComparer.OrdinalIgnoreCase);
    }
}
