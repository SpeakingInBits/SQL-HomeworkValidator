using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Tests;

public class BatchSplitterTests
{
    [Fact]
    public void Splits_on_go_lines()
    {
        var batches = BatchSplitter.Split("CREATE TABLE A (X int)\nGO\nINSERT A VALUES (1)\r\ngo  \nSELECT 1");

        Assert.Equal(3, batches.Count);
        Assert.StartsWith("CREATE TABLE", batches[0]);
        Assert.StartsWith("INSERT", batches[1]);
        Assert.StartsWith("SELECT", batches[2]);
    }

    [Fact]
    public void Go_inside_a_line_is_not_a_separator()
    {
        var batches = BatchSplitter.Split("SELECT 'GO' AS Go\nGO");

        Assert.Single(batches);
    }

    [Fact]
    public void Go_with_count_repeats_the_batch()
    {
        var batches = BatchSplitter.Split("INSERT A VALUES (1)\nGO 3");

        Assert.Equal(3, batches.Count);
    }

    [Fact]
    public void Empty_batches_are_skipped()
    {
        var batches = BatchSplitter.Split("GO\n\nGO\nSELECT 1\nGO\n");

        Assert.Single(batches);
    }
}
