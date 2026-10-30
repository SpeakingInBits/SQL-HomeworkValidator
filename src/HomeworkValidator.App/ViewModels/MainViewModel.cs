using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeworkValidator.App.Services;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.App.ViewModels;

/// <summary>
/// The window's view model. It shows one page at a time: the connection page until the student connects,
/// then the workspace. MainWindow.axaml picks the view for each page type with DataTemplates.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly ContentLibrary? _content;
    private readonly string _appDataFolder;
    private readonly AnswerStore _answers;
    private readonly IPasswordStore _passwords;
    private readonly IAppUpdater? _updater;

    public MainViewModel()
        : this(LoadContent(out var error), error, updater: new AppUpdater())
    {
    }

    /// <param name="appDataFolder">Where settings and answers are kept; tests pass their own folder.</param>
    /// <param name="passwords">Where a remembered password is kept; tests pass their own.</param>
    /// <param name="updater">Finds newer versions of the app; null (in tests) never finds one.</param>
    public MainViewModel(ContentLibrary? content, string? contentError = null, string? appDataFolder = null, IPasswordStore? passwords = null,
        IAppUpdater? updater = null)
    {
        _content = content;
        ContentError = contentError;
        _appDataFolder = appDataFolder ?? AppDataFolder.Root;
        _answers = new AnswerStore(Path.Combine(_appDataFolder, "answers"));
        _passwords = passwords ?? PasswordStore.ForThisComputer();
        _updater = updater;
        CurrentPage = new ConnectionViewModel(AppSettings.Load(_appDataFolder), _appDataFolder, OnConnected, _passwords);
    }

    /// <summary>Set when the course content couldn't be loaded; the app can't do anything useful then.</summary>
    public string? ContentError { get; }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; }

    public WorkspaceViewModel? Workspace => CurrentPage as WorkspaceViewModel;

    /// <summary>The version of a downloaded update, which installs on restart; null when there's none.</summary>
    [ObservableProperty]
    public partial string? UpdateVersion { get; set; }

    /// <summary>Saves any unsaved answers; called when the window closes.</summary>
    public void SaveAll() => Workspace?.SaveAnswers();

    /// <summary>Downloads a newer version in the background, if there is one, and offers to restart into it.</summary>
    public async Task CheckForUpdateAsync()
    {
        if (_updater is not null)
            UpdateVersion = await _updater.DownloadUpdateAsync();
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        SaveAll();
        _updater?.RestartToUpdate();
    }

    private void OnConnected(ServerConnection server, ServerInfo info)
    {
        if (_content is null)
            return;

        var workspace = new WorkspaceViewModel(_content, server, info, _answers, ChangeConnection);
        CurrentPage = workspace;
        _ = workspace.SetUpDatabasesAsync();
    }

    private void ChangeConnection()
    {
        SaveAll();
        CurrentPage = new ConnectionViewModel(AppSettings.Load(_appDataFolder), _appDataFolder, OnConnected, _passwords);
    }

    private static ContentLibrary? LoadContent(out string? error)
    {
        try
        {
            error = null;
            return ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));
        }
        catch (ContentException ex)
        {
            error = $"The homework content couldn't be loaded: {ex.Message}";
            return null;
        }
    }
}
