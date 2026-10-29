using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaloniaEdit.TextMate;
using HomeworkValidator.App.ViewModels;
using TextMateSharp.Grammars;

namespace HomeworkValidator.App.Views;

public partial class WorkspaceView : UserControl
{
    private readonly RegistryOptions _textMateOptions;
    private readonly TextMate.Installation _textMate;

    public WorkspaceView()
    {
        InitializeComponent();

        // SQL syntax highlighting. TextMate grammars are the same ones VS Code uses.
        _textMateOptions = new RegistryOptions(ThemeFor(ActualThemeVariant));
        _textMate = Editor.InstallTextMate(_textMateOptions);
        _textMate.SetGrammar(_textMateOptions.GetScopeByLanguageId("sql"));

        // Follow the operating system when it switches between light and dark.
        ActualThemeVariantChanged += (_, _) => _textMate.SetTheme(_textMateOptions.LoadTheme(ThemeFor(ActualThemeVariant)));
    }

    private static ThemeName ThemeFor(ThemeVariant variant) =>
        variant == ThemeVariant.Dark ? ThemeName.VisualStudioDark : ThemeName.VisualStudioLight;

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel workspace || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open your answer",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQL files") { Patterns = ["*.sql"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0)
            return;

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        workspace.ReplaceAnswer(await reader.ReadToEndAsync());
    }
}
