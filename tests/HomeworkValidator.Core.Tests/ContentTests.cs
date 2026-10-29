using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Tests;

/// <summary>Checks the real content folder without a database. 'hv verify' covers what needs SQL Server.</summary>
public class ContentTests
{
    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    public static TheoryData<string> QuestionIds => new(Content.Questions.Select(q => q.Id));

    [Fact]
    public void Course_databases_have_a_base_dataset_listed_first()
    {
        Assert.NotEmpty(Content.Databases);
        Assert.All(Content.Databases, db => Assert.Equal("base", db.Datasets[0].Name));
    }

    [Fact]
    public void Content_scripts_have_no_database_level_statements()
    {
        // The validator runs these inside its own databases; USE or DROP DATABASE would escape them.
        var scripts = Content.Databases.SelectMany(db => db.Datasets.Select(d => d.Sql).Append(db.SchemaSql));
        Assert.All(scripts, sql =>
        {
            Assert.DoesNotMatch(@"(?im)^\s*USE\s", sql);
            Assert.DoesNotMatch(@"(?i)\b(CREATE|DROP|ALTER)\s+DATABASE\b", sql);
        });
    }

    [Theory]
    [MemberData(nameof(QuestionIds))]
    public void Reference_solutions_are_valid_for_their_kind_and_follow_the_rules(string id)
    {
        var question = Content.GetQuestion(id);

        foreach (var sql in question.Alternatives.Select(a => a.Sql).Prepend(question.SolutionSql))
        {
            var analysis = ScriptAnalyzer.Analyze(sql, question.Kind);

            Assert.True(analysis.IsRunnable, string.Join(" ", analysis.Errors));
            Assert.Empty(ScriptAnalyzer.CheckRules(analysis.Features, question.Rules));
        }
    }

    [Fact]
    public void Dml_homework_is_loaded_with_its_setup()
    {
        var q1 = Content.GetQuestion("ap-hw2/q1");
        var q5 = Content.GetQuestion("ap-hw2/q5");

        Assert.Equal(QuestionKind.Dml, q1.Kind);
        Assert.NotNull(q1.SetupSql);
        Assert.NotNull(q1.Note);
        Assert.Equal(["ap-hw2/q1"], q5.After);
        Assert.Equal(["InvoiceCopy.PaymentDate"], q5.Compare.PresenceOnly);
    }

    [Fact]
    public void Question_ids_are_unique()
    {
        var ids = Content.Questions.Select(q => q.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
