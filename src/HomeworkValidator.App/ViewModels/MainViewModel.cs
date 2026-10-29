using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly AnswerStore _answers = new();

    public MainViewModel()
        : this(LoadContent(out var error), error)
    {
    }

    public MainViewModel(ContentLibrary? content, string? contentError = null)
    {
        _content = content;
        ContentError = contentError;
        CurrentPage = new ConnectionViewModel(AppSettings.Load(), OnConnected);
    }

    /// <summary>Set when the course content couldn't be loaded; the app can't do anything useful then.</summary>
    public string? ContentError { get; }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; }

    public WorkspaceViewModel? Workspace => CurrentPage as WorkspaceViewModel;

    /// <summary>Saves any unsaved answers; called when the window closes.</summary>
    public void SaveAll() => Workspace?.SaveAnswers();

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
        CurrentPage = new ConnectionViewModel(AppSettings.Load(), OnConnected);
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
