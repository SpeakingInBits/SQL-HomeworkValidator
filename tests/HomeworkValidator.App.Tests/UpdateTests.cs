using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HomeworkValidator.App.Services;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.App.Views;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.Tests;

public class UpdateTests
{
    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    private sealed class FakeUpdater(string? newVersion) : IAppUpdater
    {
        public bool Restarted { get; private set; }

        public Task<string?> DownloadUpdateAsync() => Task.FromResult(newVersion);

        public void RestartToUpdate() => Restarted = true;
    }

    private static MainWindow Show(MainViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1200, Height = 800 };
        window.Show();
        return window;
    }

    private static Button? RestartButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Content as string == "Restart now" && b.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task Downloaded_update_offers_a_restart()
    {
        var updater = new FakeUpdater("1.2.0");
        var viewModel = new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder(), updater: updater);
        var window = Show(viewModel);

        await viewModel.CheckForUpdateAsync();

        TestAppBuilder.SaveScreenshot(window, "update-ready");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Version 1.2.0 is ready. It installs the next time you open the app, or restart now.");
        var restart = RestartButton(window);
        Assert.NotNull(restart);

        restart.Command!.Execute(null);
        Assert.True(updater.Restarted);
    }

    [AvaloniaFact]
    public async Task No_banner_when_up_to_date()
    {
        var viewModel = new MainViewModel(Content, appDataFolder: TestAppBuilder.NewTempFolder(), updater: new FakeUpdater(null));
        var window = Show(viewModel);

        await viewModel.CheckForUpdateAsync();

        Assert.Null(RestartButton(window));
    }
}
