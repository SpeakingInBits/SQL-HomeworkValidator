using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using HomeworkValidator.App.Services;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.App.Views;
using HomeworkValidator.Core.Content;

namespace HomeworkValidator.App.Tests;

/// <summary>The connection page's "Remember my password", with an in-memory password store.</summary>
public sealed class RememberPasswordTests
{
    private readonly FakePasswordStore _store = new();
    private readonly string _appData = TestAppBuilder.NewTempFolder();

    private ConnectionViewModel Page(string server = "localhost,1433", string user = "sa") => new(
        new AppSettings { Server = server, User = user, UseWindowsAuthentication = false }, _appData, (_, _) => { }, _store);

    [Fact]
    public void A_saved_password_is_filled_in()
    {
        _store.Save("localhost,1433", "sa", "Docker123!");

        var page = Page();

        Assert.Equal("Docker123!", page.Password);
        Assert.True(page.RememberPassword);
    }

    [Fact]
    public void Without_a_saved_password_the_box_starts_unticked()
    {
        var page = Page();

        Assert.Equal("", page.Password);
        Assert.False(page.RememberPassword);
    }

    [Fact]
    public void Ticking_remember_saves_the_password_after_connecting()
    {
        var page = Page();
        page.Password = "Docker123!";
        page.RememberPassword = true;

        page.SaveOrForgetPassword();

        Assert.Equal("Docker123!", _store.Load("localhost,1433", "sa"));
    }

    [Fact]
    public void Unticking_remember_forgets_the_password()
    {
        _store.Save("localhost,1433", "sa", "Docker123!");
        var page = Page();
        page.RememberPassword = false;

        page.SaveOrForgetPassword();

        Assert.Null(_store.Load("localhost,1433", "sa"));
    }

    [Fact]
    public void Windows_authentication_leaves_saved_passwords_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows authentication is only offered on Windows.");
        _store.Save("localhost,1433", "sa", "Docker123!");
        var page = Page();
        page.UseWindowsAuthentication = true;
        page.RememberPassword = false;

        page.SaveOrForgetPassword();

        Assert.Equal("Docker123!", _store.Load("localhost,1433", "sa"));
    }

    [Fact]
    public void Nothing_is_offered_without_a_password_store()
    {
        var page = new ConnectionViewModel(new AppSettings { UseWindowsAuthentication = false }, _appData, (_, _) => { }, new NoPasswordStore());

        Assert.False(page.CanRememberPassword);
    }

    [AvaloniaFact]
    public void The_connection_page_offers_to_remember_the_password()
    {
        new AppSettings { Server = "localhost,1433", User = "sa", UseWindowsAuthentication = false }.Save(_appData);
        _store.Save("localhost,1433", "sa", "Docker123!");
        var content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));
        var window = new MainWindow { DataContext = new MainViewModel(content, appDataFolder: _appData, passwords: _store), Width = 1200, Height = 800 };
        window.Show();
        TestAppBuilder.SaveScreenshot(window, "connection-remember-password");

        var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Remember my password on this computer");
        Assert.True(box.IsEffectivelyVisible);
        Assert.True(box.IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "It's kept encrypted in a test store, not in the app's own files.");
    }

    private sealed class FakePasswordStore : IPasswordStore
    {
        private readonly Dictionary<(string Server, string User), string> _saved = [];

        public bool IsAvailable => true;
        public string Description => "a test store";
        public string? Load(string server, string user) => _saved.GetValueOrDefault((server, user));

        public bool Save(string server, string user, string password)
        {
            _saved[(server, user)] = password;
            return true;
        }

        public void Delete(string server, string user) => _saved.Remove((server, user));
    }
}
