using HomeworkValidator.Cli;
using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using Microsoft.Data.SqlClient;

const string Usage = """
    Usage: hv <command> [options]

    Commands:
      setup                        Build or update the validator's databases on your SQL Server
      list                         List assignments and questions
      show <question>              Print a question
      check <question> <file.sql>  Check your answer to a question
      clean                        Drop all databases the validator created

    Instructor commands:
      verify [assignment|question] Check reference solutions and mutants
      make-shifted <database>      Regenerate datasets/shifted.sql from base.sql. Run from the repository
                                   (or pass --content) so it writes to the source content folder.

    Options:
      --server <name>       SQL Server to connect to (default: localhost on Windows, localhost,1433 elsewhere)
      --user <login>        SQL login (default: Windows authentication on Windows, sa elsewhere)
      --password <pwd>      SQL login password (or set HV_PASSWORD)
      --connection <cs>     Full connection string, instead of the options above (or set HV_CONNECTION_STRING)
      --content <folder>    Content folder (default: the one shipped with hv)

    Question IDs look like ap-hw1/q3. Run 'hv list' to see them.
    """;

var options = Options.Parse(args);
if (options.Command is null or "help" or "--help" or "-h")
{
    Console.WriteLine(Usage);
    return options.Command is null ? 1 : 0;
}

try
{
    // make-shifted edits content, so it uses the source folder found from the current directory, not the copy next to hv.
    var contentRoot = options.ContentRoot
        ?? ContentLibrary.FindRoot(options.Command == "make-shifted" ? Environment.CurrentDirectory : AppContext.BaseDirectory);
    var content = ContentLibrary.Load(contentRoot);
    var server = options.CreateConnection();
    var databases = new DatabaseManager(server);
    var checker = new Checker(content, databases, server);
    var output = new ConsoleOutput();

    return options.Command switch
    {
        "list" => Commands.List(content, output),
        "show" => Commands.Show(content, options.Arg(0, "question"), output),
        "setup" => await Commands.SetupAsync(content, databases, output),
        "check" => await Commands.CheckAsync(content, checker, options.Arg(0, "question"), options.Arg(1, "file.sql"), output),
        "verify" => await Commands.VerifyAsync(content, new Verifier(checker), options.OptionalArg(0), output),
        "clean" => await Commands.CleanAsync(databases, output),
        "make-shifted" => Commands.MakeShifted(content, options.Arg(0, "database"), output),
        _ => throw new UsageException($"Unknown command '{options.Command}'."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}
catch (Exception ex) when (ex is ContentException or QuestionConfigurationException)
{
    Console.Error.WriteLine($"Content problem: {ex.Message}");
    return 2;
}
catch (SqlException ex)
{
    Console.Error.WriteLine($"SQL Server error: {ex.Message}");
    Console.Error.WriteLine("Check that SQL Server is running and the --server, --user and --password options are right.");
    return 2;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
