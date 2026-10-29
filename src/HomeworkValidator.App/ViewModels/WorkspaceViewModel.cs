using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeworkValidator.App.Services;
using HomeworkValidator.Core.Checking;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;
using Microsoft.Data.SqlClient;

namespace HomeworkValidator.App.ViewModels;

/// <summary>The main page: assignments on the left, the selected question with its editor and results on the right.</summary>
public partial class WorkspaceViewModel : ViewModelBase
{
    /// <summary>Rows of each kind to show when a result doesn't match. The command line shows 5; the app has room for more.</summary>
    private const int SampleSize = 25;

    private readonly ContentLibrary _content;
    private readonly DatabaseManager _databases;
    private readonly Checker _checker;
    private readonly AnswerStore _answers;
    private readonly Action _changeConnection;
    private readonly DispatcherTimer _autoSave;
    private readonly DispatcherTimer _fileWatch;

    public WorkspaceViewModel(ContentLibrary content, ServerConnection server, ServerInfo info, AnswerStore answers, Action changeConnection)
    {
        _content = content;
        _answers = answers;
        _changeConnection = changeConnection;
        _databases = new DatabaseManager(server);
        _checker = new Checker(content, _databases, server) { SampleSize = SampleSize };

        ServerDescription = $"{server.Server} · SQL Server {info.Version} {info.Edition}";
        Assignments = new ObservableCollection<AssignmentViewModel>(
            content.Assignments.Select(a => new AssignmentViewModel(a, answers)));
        SelectedNode = Assignments.FirstOrDefault()?.Questions.FirstOrDefault();

        // Answers are saved every few seconds while the student types, and again when they check or switch questions.
        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoSave.Tick += (_, _) => SaveAnswers();
        _autoSave.Start();

        // Answers opened from a .sql file follow that file when it's saved in another editor.
        _fileWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fileWatch.Tick += (_, _) => RefreshLinkedFiles();
        _fileWatch.Start();
    }

    public string ServerDescription { get; }

    public ObservableCollection<AssignmentViewModel> Assignments { get; }

    /// <summary>The tree's selection: an assignment or a question.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedQuestion))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial object? SelectedNode { get; set; }

    public QuestionViewModel? SelectedQuestion => SelectedNode as QuestionViewModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetUpDatabasesCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>What the app is doing or the state of the databases, shown in the top bar.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool DatabasesReady { get; set; }

    /// <summary>A problem that isn't about the student's answer: connection lost, broken content, a database name clash.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    partial void OnSelectedNodeChanged(object? oldValue, object? newValue) => SaveAnswers();

    /// <summary>Creates or updates the validator's HV_ databases. Runs on connect and from the Set up button.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task SetUpDatabasesAsync()
    {
        IsBusy = true;
        Problem = null;
        try
        {
            var pending = (await _databases.GetStatusAsync(_content)).Where(s => s.State != DatabaseState.Current).ToList();
            for (var i = 0; i < pending.Count; i++)
            {
                Status = $"Setting up {pending[i].Name} ({i + 1} of {pending.Count})...";
                await _databases.EnsureAsync(pending[i].Database, pending[i].Dataset);
            }

            DatabasesReady = true;
            Status = "Databases ready";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ContentException)
        {
            Status = "Databases not set up";
            Problem = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (SelectedQuestion is not { } question)
            return;

        // A file saved a moment ago in another editor may not have been picked up yet.
        question.RefreshLinkedFile();
        question.Save(_answers);
        IsBusy = true;
        Problem = null;
        Status = $"Checking {question.Question.Id}...";
        try
        {
            var report = await _checker.CheckAsync(question.Question, question.Document.Text);
            question.Result = new ResultViewModel(report);
            question.Status = report.Passed ? QuestionStatus.Passed : QuestionStatus.Failed;
            _answers.SetStatus(question.Question, question.Status);
            DatabasesReady = true;
            Status = "Databases ready";
        }
        catch (QuestionConfigurationException ex)
        {
            Problem = $"This question's content has a problem, so it can't be checked. Tell your instructor: {ex.Message}";
            Status = "";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ContentException)
        {
            Problem = ex.Message;
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ChangeConnection()
    {
        _autoSave.Stop();
        _fileWatch.Stop();
        _changeConnection();
    }

    /// <summary>Replaces the selected question's answer, e.g. with a .sql file the student opened.</summary>
    public void ReplaceAnswer(string sql)
    {
        if (SelectedQuestion is { } question)
            question.Document.Text = sql;
    }

    /// <summary>Loads a .sql file into the selected question and keeps it in step with the file.</summary>
    public void OpenFile(string path)
    {
        try
        {
            SelectedQuestion?.OpenFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = $"Couldn't open {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void LoadChangedFile() => SelectedQuestion?.LoadChangedFile();

    [RelayCommand]
    private void KeepEdits() => SelectedQuestion?.KeepEdits();

    [RelayCommand]
    private void UnlinkFile() => SelectedQuestion?.Unlink();

    public void SaveAnswers()
    {
        foreach (var question in Assignments.SelectMany(a => a.Questions))
            question.Save(_answers);
    }

    public void RefreshLinkedFiles()
    {
        foreach (var question in Assignments.SelectMany(a => a.Questions))
            question.RefreshLinkedFile();
    }

    private bool IsIdle() => !IsBusy;
    private bool CanCheck() => !IsBusy && SelectedQuestion is not null;
}
