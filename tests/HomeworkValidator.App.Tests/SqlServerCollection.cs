namespace HomeworkValidator.App.Tests;

/// <summary>
/// Tests that use a SQL Server run one at a time. Scripts run in transactions, so running them next to each other deadlocks
/// on the validator's databases. In the app that can't happen: it does one thing at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerCollection
{
    public const string Name = "SQL Server";
}
