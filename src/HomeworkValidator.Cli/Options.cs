using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.Cli;

public sealed class UsageException(string message) : Exception(message);

/// <summary>Command-line arguments: a command, positional arguments and a few --name value options.</summary>
public sealed class Options
{
    private static readonly string[] Known = ["--server", "--user", "--password", "--connection", "--content"];

    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

    public string? Command => _positional.Count > 0 ? _positional[0] : null;
    public string? ContentRoot => _named.GetValueOrDefault("--content");

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && Known.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    throw new UsageException($"{arg} needs a value.");
                options._named[arg] = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg is not "--help")
            {
                throw new UsageException($"Unknown option '{arg}'.");
            }
            else
            {
                options._positional.Add(arg);
            }
        }

        return options;
    }

    /// <summary>Positional argument after the command, required.</summary>
    public string Arg(int index, string name) =>
        OptionalArg(index) ?? throw new UsageException($"'{Command}' needs a {name} argument.");

    public string? OptionalArg(int index) => index + 1 < _positional.Count ? _positional[index + 1] : null;

    public ServerConnection CreateConnection()
    {
        var connectionString = _named.GetValueOrDefault("--connection") ?? Environment.GetEnvironmentVariable("HV_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connectionString))
            return new ServerConnection(connectionString);

        return ServerConnection.Create(
            _named.GetValueOrDefault("--server"),
            _named.GetValueOrDefault("--user"),
            _named.GetValueOrDefault("--password") ?? Environment.GetEnvironmentVariable("HV_PASSWORD"));
    }
}
