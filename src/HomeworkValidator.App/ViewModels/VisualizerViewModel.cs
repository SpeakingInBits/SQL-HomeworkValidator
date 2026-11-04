using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeworkValidator.Core.Visualization;

namespace HomeworkValidator.App.ViewModels;

/// <summary>Plays a query's animation: which step is showing, and the Previous, Play, Next and Restart buttons.</summary>
public partial class VisualizerViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer = new();

    /// <param name="note">Shown above the animation, e.g. when only some of the rows are shown.</param>
    public VisualizerViewModel(VizScene scene, string? note = null)
    {
        Scene = scene;
        Note = note;
        _timer.Tick += (_, _) => Advance();
        UpdateInterval();
    }

    public VizScene Scene { get; }
    public string? Note { get; }

    /// <summary>The in-depth view is about joins, so other queries don't offer it.</summary>
    public bool ShowsInDepth => Scene.HasJoin;
    public bool CanUseInDepth => Scene.DetailFrames is not null;
    public string InDepthTip => Scene.DetailUnavailable ?? "Shows every comparison the join makes, including the ones where ON is false.";

    private IReadOnlyList<VizFrame> Frames => InDepth && Scene.DetailFrames is { } detail ? detail : Scene.Frames;

    public VizFrame Frame => Frames[Step];
    public string Phase => Frame.Phase;
    public string StepText => $"Step {Step + 1} of {Frames.Count}";
    public string PlayLabel => IsPlaying ? "Pause" : "Play";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frame), nameof(Phase), nameof(StepText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    public partial int Step { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    public partial bool IsPlaying { get; set; }

    /// <summary>From 1 to 10, set with the slider.</summary>
    [ObservableProperty]
    public partial int Speed { get; set; } = 5;

    partial void OnSpeedChanged(int value) => UpdateInterval();

    [ObservableProperty]
    public partial bool InDepth { get; set; }

    // The two modes have different steps, so switching starts over.
    partial void OnInDepthChanged(bool value)
    {
        Pause();
        // Step may not change, but the frames it points at have.
        Step = 0;
        OnPropertyChanged(nameof(Frame));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(StepText));
        NextCommand.NotifyCanExecuteChanged();
    }

    private void UpdateInterval() => _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(120, 1100 - Speed * 100));

    private bool CanGoBack() => Step > 0;
    private bool CanGoForward() => Step < Frames.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Previous()
    {
        Pause();
        Step--;
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Next()
    {
        Pause();
        Step++;
    }

    [RelayCommand]
    private void Restart()
    {
        Pause();
        Step = 0;
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }

        if (Step >= Frames.Count - 1)
            Step = 0;
        IsPlaying = true;
        _timer.Start();
    }

    private void Advance()
    {
        if (CanGoForward())
            Step++;
        if (!CanGoForward())
            Pause();
    }

    /// <summary>Stops playing, also when the window closes.</summary>
    public void Pause()
    {
        _timer.Stop();
        IsPlaying = false;
    }
}
