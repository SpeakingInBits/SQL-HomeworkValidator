using Avalonia.Headless.XUnit;
using Avalonia.Input;
using HomeworkValidator.App.Services;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.Core.Content;
using HomeworkValidator.Core.Execution;

namespace HomeworkValidator.App.Tests;

/// <summary>Suggestions in the SQL editor, turned on from the Settings menu.</summary>
public class AutoCompleteTests
{
    private static readonly ContentLibrary Content = ContentLibrary.Load(ContentLibrary.FindRoot(AppContext.BaseDirectory));

    private readonly string _appData = TestAppBuilder.NewTempFolder();

    /// <summary>The workspace on an AP question. Nothing connects until a check runs, so a made-up server is fine.</summary>
    private AppDriver Launch(AppSettings settings, string name)
    {
        var workspace = new WorkspaceViewModel(Content, ServerConnection.Create("example-server", "sa", "x"),
            new ServerInfo("17.0.1000.7", "Developer Edition (64-bit)", true), new AnswerStore(TestAppBuilder.NewTempFolder()), () => { },
            settings, () => settings.Save(_appData));
        workspace.SelectedNode = workspace.Assignments.SelectMany(a => a.Questions).First(q => q.Question.Database == "AP");
        return AppDriver.Launch(new MainViewModel(Content, appDataFolder: _appData) { CurrentPage = workspace }, name);
    }

    /// <summary>Writes the SQL with the caret at the |, the way a student goes back to fill in a SELECT list.</summary>
    private static void WriteWithCaret(AppDriver app, string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        app.WriteAnswer(sqlWithCaret.Remove(caret, 1));
        app.Editor.CaretOffset = caret;
    }

    [AvaloniaFact]
    public void Settings_menu_turns_autocomplete_on_and_remembers_it()
    {
        var settings = new AppSettings();
        var app = Launch(settings, "autocomplete-setting");

        app.Click(app.Button("Settings"));
        var item = app.MenuItem("Settings", "Autocomplete as you type");
        app.Screenshot("settings-menu");
        Assert.False(item.IsChecked); // off until the student turns it on

        app.Click(item);

        Assert.True(settings.AutoComplete);
        Assert.True(AppSettings.Load(_appData).AutoComplete);
    }

    [AvaloniaFact]
    public void Select_list_suggests_columns_from_the_from_clause()
    {
        var app = Launch(new AppSettings { AutoComplete = true }, "autocomplete-columns");
        WriteWithCaret(app, "SELECT | FROM Vendors");

        foreach (var key in "VendorN") // one key at a time, as a student types
            app.Type(key.ToString());
        app.Screenshot("suggestions");
        app.Press(PhysicalKey.Tab);

        Assert.Equal("SELECT VendorName FROM Vendors", app.Editor.Document.Text);
    }

    [AvaloniaFact]
    public void Alias_dot_suggests_that_tables_columns()
    {
        var app = Launch(new AppSettings { AutoComplete = true }, "autocomplete-alias");
        WriteWithCaret(app, "SELECT | FROM Invoices i JOIN Vendors v ON i.VendorID = v.VendorID");

        app.Type("i.InvoiceT");
        app.Press(PhysicalKey.Enter);

        Assert.StartsWith("SELECT i.InvoiceTotal FROM", app.Editor.Document.Text);
    }

    [AvaloniaFact]
    public void Ctrl_space_shows_suggestions()
    {
        var app = Launch(new AppSettings { AutoComplete = true }, "autocomplete-ctrl-space");
        WriteWithCaret(app, "SELECT VendorSt| FROM Vendors");

        app.Press(PhysicalKey.Space, RawInputModifiers.Control);
        app.Press(PhysicalKey.Tab);

        Assert.Equal("SELECT VendorState FROM Vendors", app.Editor.Document.Text);
    }

    [AvaloniaFact]
    public void Space_closes_the_suggestions_without_choosing_one()
    {
        var app = Launch(new AppSettings { AutoComplete = true }, "autocomplete-space");
        WriteWithCaret(app, "SELECT * FROM Vendors |");

        app.Type("v WHERE v.VendorID = 1");

        Assert.Equal("SELECT * FROM Vendors v WHERE v.VendorID = 1", app.Editor.Document.Text);
    }

    [AvaloniaFact]
    public void Nothing_is_suggested_when_autocomplete_is_off()
    {
        var app = Launch(new AppSettings(), "autocomplete-off");
        WriteWithCaret(app, "SELECT | FROM Vendors");

        app.Type("VendorN");
        app.Press(PhysicalKey.Space, RawInputModifiers.Control);
        app.Press(PhysicalKey.Tab);

        Assert.StartsWith("SELECT VendorN", app.Editor.Document.Text);
        Assert.DoesNotContain("VendorName", app.Editor.Document.Text);
    }
}
