using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Sql;

namespace HomeworkValidator.Core.Tests;

public class DdlAnalyzerTests
{
    [Fact]
    public void Typical_script_skips_the_database_statements_and_runs_the_rest()
    {
        const string sql = """
            USE master;
            GO
            IF DB_ID('Membership') IS NOT NULL
                DROP DATABASE Membership;
            GO
            CREATE DATABASE Membership;
            GO
            USE Membership;

            CREATE TABLE Groups (GroupID int IDENTITY PRIMARY KEY, GroupName varchar(50));
            """;

        var result = ScriptAnalyzer.AnalyzeDdl(sql, "Membership");

        Assert.True(result.IsRunnable, string.Join(" ", result.Errors));
        Assert.Contains(SqlFeature.CreateDatabase, result.Features);
        var batch = Assert.Single(result.Batches);
        Assert.DoesNotContain("DATABASE", batch);
        Assert.DoesNotContain("USE", batch);
        Assert.Contains("CREATE TABLE Groups", batch);
        // The CREATE TABLE is still on line 10, so SQL Server's line numbers match the student's file.
        Assert.Equal(10, batch.Split('\n').ToList().FindIndex(l => l.Contains("CREATE TABLE")) + 1);
        Assert.Contains(result.Notes, n => n.Contains("never creates or drops databases"));
    }

    [Fact]
    public void Only_create_database_is_runnable_with_nothing_to_run()
    {
        var result = ScriptAnalyzer.AnalyzeDdl("CREATE DATABASE Membership;", "Membership");

        Assert.True(result.IsRunnable);
        Assert.True(result.NothingToRun);
        Assert.Empty(result.Batches);
    }

    [Theory]
    [InlineData("CREATE DATABASE Members;")]
    [InlineData("DROP DATABASE AP;")]
    [InlineData("IF DB_ID('AP') IS NOT NULL DROP DATABASE AP;")]
    [InlineData("ALTER DATABASE AP SET SINGLE_USER;")]
    public void Statements_on_other_databases_are_refused(string sql)
    {
        var result = ScriptAnalyzer.AnalyzeDdl(sql, "Membership");

        Assert.False(result.IsRunnable);
        Assert.Contains(result.Errors, e => e.Contains("this question uses the Membership database"));
    }

    [Fact]
    public void Database_statements_are_refused_in_dml_scripts()
    {
        var result = ScriptAnalyzer.Analyze("CREATE DATABASE Membership;", QuestionKind.Dml, "Membership");

        Assert.False(result.IsRunnable);
    }

    [Theory]
    [InlineData("CREATE INDEX IX_A ON GroupMembership (GroupID);")]
    [InlineData("CREATE CLUSTERED INDEX IX_A ON GroupMembership (GroupID);")]
    [InlineData("ALTER TABLE Individuals ADD DuesPaid bit NOT NULL DEFAULT 0;")]
    [InlineData("ALTER TABLE Invoices WITH NOCHECK ADD CHECK (PaymentTotal >= 0);")]
    [InlineData("ALTER TABLE Invoices DROP CONSTRAINT CK_Something;")]
    [InlineData("DROP INDEX IX_A ON GroupMembership;")]
    [InlineData("CREATE VIEW V AS SELECT 1 AS One;")]
    public void Design_statements_are_allowed(string sql)
    {
        var result = ScriptAnalyzer.AnalyzeDdl(sql, "Membership");

        Assert.True(result.IsRunnable, string.Join(" ", result.Errors));
    }

    [Theory]
    [InlineData("EXEC sp_who;")]
    [InlineData("CREATE LOGIN hacker WITH PASSWORD = 'x';")]
    [InlineData("BACKUP DATABASE Membership TO DISK = 'x.bak';")]
    public void Statements_that_reach_outside_the_database_are_still_refused(string sql)
    {
        Assert.False(ScriptAnalyzer.AnalyzeDdl(sql, "Membership").IsRunnable);
    }

    [Fact]
    public void An_if_that_guards_other_work_is_not_skipped()
    {
        var result = ScriptAnalyzer.AnalyzeDdl(
            "IF OBJECT_ID('GroupMembership') IS NOT NULL DROP TABLE GroupMembership;", "Membership");

        Assert.True(result.IsRunnable);
        Assert.Contains("DROP TABLE GroupMembership", Assert.Single(result.Batches));
    }
}
