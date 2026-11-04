using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.Core.Tests;

public class SetShapeTests
{
    private static SetShape Shape(string sql) => Assert.IsType<SetShape>(SetShape.Analyze(sql));

    [Fact]
    public void Other_queries_arent_its_business()
    {
        Assert.Null(SetShape.Analyze("SELECT VendorName FROM Vendors"));
        Assert.Null(SetShape.Analyze("SELEC VendorName FROM Vendors"));
        Assert.Null(SetShape.Analyze("WITH v AS (SELECT * FROM Vendors) SELECT * FROM v UNION SELECT * FROM v"));
    }

    [Theory]
    [InlineData("UNION", "UNION")]
    [InlineData("UNION ALL", "UNION ALL")]
    [InlineData("INTERSECT", "INTERSECT")]
    [InlineData("EXCEPT", "EXCEPT")]
    public void Reads_each_operator(string keyword, string name)
    {
        var shape = Shape($"SELECT VendorID FROM Vendors {keyword} SELECT VendorID FROM Invoices");

        Assert.Equal(["SELECT VendorID FROM Vendors", "SELECT VendorID FROM Invoices"], shape.Branches);
        var step = Assert.Single(shape.Steps);
        Assert.Equal(name, step.Operator);
        Assert.Equal((0, 1), (step.Left, step.Right));
        Assert.Equal($"Query 1 {name} Query 2", shape.Description);
    }

    [Fact]
    public void ORDER_BY_sorts_the_end_result_and_isnt_part_of_the_operator()
    {
        var shape = Shape("""
            SELECT VendorName, 'CA' AS State FROM Vendors WHERE VendorState = 'CA'
            UNION
            SELECT VendorName, 'Other' FROM Vendors WHERE VendorState <> 'CA'
            ORDER BY VendorName DESC, State;
            """);

        Assert.Equal(["VendorName DESC", "State"], shape.SortKeys);
        Assert.DoesNotContain("ORDER BY", Assert.Single(shape.Steps).Sql);
        Assert.DoesNotContain("ORDER BY", shape.Branches[1]);
        Assert.Contains("ORDER BY", shape.Query);
    }

    [Fact]
    public void A_chain_runs_from_left_to_right()
    {
        var shape = Shape("SELECT 1 AS N UNION SELECT 2 EXCEPT SELECT 3");

        Assert.Equal(3, shape.Branches.Count);
        Assert.Equal(["UNION", "EXCEPT"], shape.Steps.Select(s => s.Operator));

        // Table 3 is the first step's result, which the second step reads.
        Assert.Equal((0, 1), (shape.Steps[0].Left, shape.Steps[0].Right));
        Assert.Equal((3, 2), (shape.Steps[1].Left, shape.Steps[1].Right));
        Assert.Equal("SELECT 1 AS N UNION SELECT 2", shape.Steps[0].Sql);
        Assert.Equal("SELECT 1 AS N UNION SELECT 2 EXCEPT SELECT 3", shape.Steps[1].Sql);
    }

    [Fact]
    public void INTERSECT_runs_first_as_SQL_Server_does()
    {
        var shape = Shape("SELECT 1 AS N UNION SELECT 2 INTERSECT SELECT 2");

        Assert.Equal(["INTERSECT", "UNION"], shape.Steps.Select(s => s.Operator));
        Assert.Equal((1, 2), (shape.Steps[0].Left, shape.Steps[0].Right));
        Assert.Equal((0, 3), (shape.Steps[1].Left, shape.Steps[1].Right));
    }

    [Fact]
    public void Several_INTERSECTs_run_before_what_surrounds_them()
    {
        var shape = Shape("SELECT 1 AS N EXCEPT SELECT 2 INTERSECT SELECT 3 INTERSECT SELECT 4 UNION SELECT 5");

        Assert.Equal(["INTERSECT", "INTERSECT", "EXCEPT", "UNION"], shape.Steps.Select(s => s.Operator));
        Assert.Equal("SELECT 2 INTERSECT SELECT 3", shape.Steps[0].Sql);
        Assert.Equal("SELECT 2 INTERSECT SELECT 3 INTERSECT SELECT 4", shape.Steps[1].Sql);
        Assert.Equal("SELECT 1 AS N EXCEPT SELECT 2 INTERSECT SELECT 3 INTERSECT SELECT 4", shape.Steps[2].Sql);
    }

    [Fact]
    public void Parentheses_decide_what_runs_first()
    {
        var shape = Shape("SELECT 1 AS N EXCEPT (SELECT 2 UNION SELECT 3)");

        Assert.Equal(["UNION", "EXCEPT"], shape.Steps.Select(s => s.Operator));
        Assert.Equal((0, 3), (shape.Steps[1].Left, shape.Steps[1].Right));
        Assert.Equal("Query 1 EXCEPT (Query 2 UNION Query 3)", shape.Description);
    }

    [Fact]
    public void Says_what_it_cant_show()
    {
        var problem = Assert.Single(Shape("SELECT 1 AS N UNION SELECT 2 ORDER BY N OFFSET 1 ROWS").Problems);

        Assert.Contains("OFFSET", problem);
    }
}
