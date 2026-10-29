using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeworkValidator.App.Services;
using HomeworkValidator.Core.Execution;
using Microsoft.Data.SqlClient;

namespace HomeworkValidator.App.ViewModels;

/// <summary>Collects the server and login, tests them, and hands a working connection to <see cref="MainViewModel"/>.</summary>
public partial class ConnectionViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly string _appDataFolder;
    private readonly Action<ServerConnection, ServerInfo> _connected;

    public ConnectionViewModel(AppSettings settings, string appDataFolder, Action<ServerConnection, ServerInfo> connected)
    {
        _settings = settings;
        _appDataFolder = appDataFolder;
        _connected = connected;
        Server = settings.Server ?? DefaultServer;
        UseWindowsAuthentication = CanUseWindowsAuthentication && settings.UseWindowsAuthentication;
        User = settings.User ?? "sa";
    }

    public static string DefaultServer => OperatingSystem.IsWindows() ? "localhost" : "localhost,1433";

    /// <summary>Windows authentication only works on Windows; everywhere else it's SQL authentication (the Docker image's sa login).</summary>
    public bool CanUseWindowsAuthentication => OperatingSystem.IsWindows();

    public string ServerHelp => OperatingSystem.IsWindows()
        ? "If you installed SQL Server on this computer, this is usually localhost (or localhost\\SQLEXPRESS for Express) with Windows authentication."
        : "If SQL Server runs in Docker on this computer, this is usually localhost,1433 with the sa login and the password you gave the container.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Server { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSqlAuthentication))]
    public partial bool UseWindowsAuthentication { get; set; }

    public bool UseSqlAuthentication
    {
        get => !UseWindowsAuthentication;
        set => UseWindowsAuthentication = !value;
    }

    [ObservableProperty]
    public partial string User { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The technical message behind <see cref="Error"/>, for when the plain explanation isn't enough.</summary>
    [ObservableProperty]
    public partial string? ErrorDetails { get; set; }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        Error = null;
        ErrorDetails = null;
        var server = ServerConnection.Create(
            Server.Trim(),
            UseWindowsAuthentication ? null : User.Trim(),
            UseWindowsAuthentication ? null : Password);

        try
        {
            var info = await server.TestAsync();
            if (!info.CanCreateDatabases)
            {
                Error = "Connected, but this login isn't allowed to create databases, which the validator needs. " +
                        "Use an administrator login such as sa.";
                return;
            }

            _settings.Server = Server.Trim();
            _settings.UseWindowsAuthentication = UseWindowsAuthentication;
            _settings.User = User.Trim();
            _settings.Save(_appDataFolder);

            _connected(server, info);
        }
        catch (SqlException ex)
        {
            Error = Explain(ex);
            ErrorDetails = ex.Message;
        }
    }

    /// <summary>A plain-language reason first; SqlClient's own message is shown underneath as details.</summary>
    private string Explain(SqlException ex) => ex.Number switch
    {
        // 18456: wrong login or password.
        18456 => $"{Server.Trim()} rejected the login. Check the login name and password.",
        // 233: the server answered but dropped the login. A server set up for Windows authentication only does this to SQL logins.
        233 when OperatingSystem.IsWindows() =>
            $"{Server.Trim()} rejected the login. Check the login name and password. If you normally sign in with " +
            "Windows authentication, choose that instead: this server may not accept SQL Server logins.",
        233 => $"{Server.Trim()} rejected the login. Check the login name and password.",
        _ when OperatingSystem.IsWindows() =>
            $"Couldn't reach {Server.Trim()}. Check that SQL Server is running and that the server name is right.",
        _ => $"Couldn't reach {Server.Trim()}. Check that your SQL Server container is running (docker ps) and that the server name is right.",
    };

    private bool CanConnect() => !string.IsNullOrWhiteSpace(Server);
}
