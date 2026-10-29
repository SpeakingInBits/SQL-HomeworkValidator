using HomeworkValidator.Core.Content;

namespace HomeworkValidator.Core.Tests;

/// <summary>Loading rules, tested against small content folders built in a temp directory.</summary>
public sealed class ContentLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hv-tests-" + Guid.NewGuid().ToString("N"));

    public ContentLibraryTests()
    {
        Write("databases/Shop/schema.sql", "CREATE TABLE Items (ItemID int)");
        Write("databases/Shop/datasets/base.sql", "INSERT Items VALUES (1)");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string text)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Include_lines_insert_the_named_file()
    {
        Write("databases/Shop/datasets/edge.sql", "-- edge\n:r base.sql\nINSERT Items VALUES (2)");

        var edge = ContentLibrary.Load(_root).GetDatabase("Shop").GetDataset("edge");

        Assert.Contains("INSERT Items VALUES (1)", edge.Sql);
        Assert.Contains("INSERT Items VALUES (2)", edge.Sql);
        Assert.DoesNotContain(":r", edge.Sql);
    }

    [Fact]
    public void Include_cycles_are_reported()
    {
        Write("databases/Shop/datasets/a.sql", ":r b.sql");
        Write("databases/Shop/datasets/b.sql", ":r a.sql");

        var ex = Assert.Throws<ContentException>(() => ContentLibrary.Load(_root));
        Assert.Contains("includes itself", ex.Message);
    }

    [Fact]
    public void Dataset_hints_are_loaded_and_checked()
    {
        Write("databases/Shop/datasets/hints.yaml", "base: Look closely.");

        Assert.Equal("Look closely.", ContentLibrary.Load(_root).GetDatabase("Shop").GetDataset("base").Hint);

        Write("databases/Shop/datasets/hints.yaml", "nope: Not a dataset.");
        Assert.Throws<ContentException>(() => ContentLibrary.Load(_root));
    }

    [Fact]
    public void Base_dataset_comes_first()
    {
        Write("databases/Shop/datasets/aaa.sql", "INSERT Items VALUES (3)");

        var datasets = ContentLibrary.Load(_root).GetDatabase("Shop").Datasets;

        Assert.Equal(["base", "aaa"], datasets.Select(d => d.Name));
    }

    [Fact]
    public void Question_options_are_parsed()
    {
        Write("assignments/hw1/assignment.yaml", "title: Homework 1\ndatabase: Shop");
        Write("assignments/hw1/q2.sql", "SELECT ItemID FROM Items ORDER BY ItemID;");
        Write("assignments/hw1/q2.yaml", """
            title: Items
            prompt: List items.
            compare:
              decimals: 2
              order:
                - ItemID
                - column: Name
                  keyPattern: "^[^,]*"
            rules:
              forbid: [AliasWithoutAs]
            hints:
              base: A hint.
            """);

        var question = ContentLibrary.Load(_root).GetQuestion("hw1/q2");

        Assert.Equal(2, question.Compare.Decimals);
        Assert.Equal(["ItemID", "Name"], question.Compare.Order.Select(o => o.Column));
        Assert.Null(question.Compare.Order[0].KeyPattern);
        Assert.Equal("^[^,]*", question.Compare.Order[1].KeyPattern!.ToString());
        Assert.Contains(SqlFeature.AliasWithoutAs, question.Rules.Forbid);
        Assert.Equal(["base"], question.Datasets);
        Assert.Equal("A hint.", question.Hints["base"]);
    }

    [Fact]
    public void Unknown_question_fields_are_rejected()
    {
        Write("assignments/hw1/assignment.yaml", "title: Homework 1\ndatabase: Shop");
        Write("assignments/hw1/q1.sql", "SELECT 1;");
        Write("assignments/hw1/q1.yaml", "title: T\nprompt: P\ncompre:\n  decimals: 2");

        Assert.Throws<ContentException>(() => ContentLibrary.Load(_root));
    }
}
