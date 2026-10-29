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
    private readonly Action<ServerConnection, ServerInfo> _connected;

    public ConnectionViewModel(AppSettings settings, Action<ServerConnection, ServerInfo> connected)
    {
        _settings = settings;
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

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        Error = null;
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
            _settings.Save();

            _connected(server, info);
        }
        catch (SqlException ex)
        {
            Error = $"Couldn't connect to {Server.Trim()}: {ex.Message}";
        }
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(Server);
}
