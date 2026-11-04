using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using HomeworkValidator.App.ViewModels;
using HomeworkValidator.Core.Sql;
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

        // Suggestions while typing, when turned on in the Settings menu. Ctrl+Space is handled before the editor sees it.
        Editor.TextArea.TextEntering += Editor_TextEntering;
        Editor.TextArea.TextEntered += Editor_TextEntered;
        Editor.TextArea.AddHandler(KeyDownEvent, Editor_KeyDown, RoutingStrategies.Tunnel);
        Editor.DocumentChanged += (_, _) => _completion?.Close();
    }

    private WorkspaceViewModel? _workspace;
    private VisualizerWindow? _visualizer;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _workspace?.VisualizerReady -= ShowVisualizer;
        _workspace = DataContext as WorkspaceViewModel;
        _workspace?.VisualizerReady += ShowVisualizer;
    }

    /// <summary>Opens the visualizer, or shows the new animation in the one that's already open.</summary>
    private void ShowVisualizer(VisualizerViewModel animation)
    {
        if (_visualizer is { } open)
        {
            (open.DataContext as VisualizerViewModel)?.Pause();
            open.DataContext = animation;
            open.Activate();
            return;
        }

        var window = new VisualizerWindow { DataContext = animation };
        window.Closed += (_, _) => _visualizer = null;
        _visualizer = window;
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    private CompletionWindow? _completion;

    private bool AutoComplete => DataContext is WorkspaceViewModel { AutoComplete: true };

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#';

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control && AutoComplete)
        {
            ShowCompletion();
            e.Handled = true;
        }
    }

    private void Editor_TextEntering(object? sender, TextInputEventArgs e)
    {
        // Only Tab and Enter choose a suggestion. Anything else that ends a name (space, comma, bracket) closes the list,
        // so typing an alias or a keyword the list didn't expect never puts something else in its place.
        if (_completion is not null && e.Text is { Length: > 0 } text && !IsWordChar(text[0]))
            _completion.Close();
    }

    private void Editor_TextEntered(object? sender, TextInputEventArgs e)
    {
        if (!AutoComplete || e.Text is not { Length: > 0 } text)
            return;

        if (_completion is not null)
        {
            CloseIfNothingLeftToSuggest();
            return;
        }

        // The first letter of a name lists everything that fits there, and a dot lists the table's columns.
        // Only when that letter or dot was just typed: editing the middle of a name already written shouldn't pop up a list.
        var caret = Editor.CaretOffset;
        var wordStart = caret;
        while (wordStart > 0 && IsWordChar(Editor.Document.GetCharAt(wordStart - 1)))
            wordStart--;
        var startsName = wordStart < caret && (char.IsLetter(Editor.Document.GetCharAt(wordStart)) || Editor.Document.GetCharAt(wordStart) == '_');
        var afterDot = wordStart > 0 && Editor.Document.GetCharAt(wordStart - 1) == '.';
        var justTyped = afterDot ? wordStart - 1 >= caret - text.Length : wordStart >= caret - text.Length;
        if (justTyped && (startsName || afterDot))
            ShowCompletion();
    }

    private void ShowCompletion()
    {
        if (DataContext is not WorkspaceViewModel workspace)
            return;

        _completion?.Close();
        var caret = Editor.CaretOffset;
        var result = SqlCompletion.Complete(Editor.Document.Text, caret, workspace.Catalog);
        var typed = Editor.Document.GetText(result.Start, caret - result.Start);
        if (!result.Items.Any(i => i.Text.Contains(typed, StringComparison.OrdinalIgnoreCase)))
            return;

        var window = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = result.Start,
            CloseWhenCaretAtBeginning = result.Start < caret,
        };
        foreach (var item in result.Items)
            window.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        window.CompletionList.SelectItem(typed);
        window.Closed += (_, _) =>
        {
            if (_completion == window)
                _completion = null;
        };
        _completion = window;
        window.Show();
    }

    /// <summary>Closes the list once nothing in it fits what's typed, or the name is already typed out in full.</summary>
    private void CloseIfNothingLeftToSuggest()
    {
        if (_completion is null || Editor.CaretOffset < _completion.StartOffset)
            return;

        var typed = Editor.Document.GetText(_completion.StartOffset, Editor.CaretOffset - _completion.StartOffset);
        var matches = _completion.CompletionList.CompletionData.Where(d => d.Text.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0 || (matches.Count == 1 && matches[0].Text.Equals(typed, StringComparison.OrdinalIgnoreCase)))
            _completion.Close();
    }

    /// <summary>One suggestion in the list. Its detail (type and table, "alias for Vendors") shows beside the selected one.</summary>
    private sealed class SqlCompletionData(CompletionItem item) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => item.Text;
        public object Content => item.Text;
        public object Description => item.Detail;

        // When several suggestions fit equally well, a column wins over a table, a function or a keyword.
        public double Priority => item.Kind switch
        {
            CompletionKind.Column => 3,
            CompletionKind.Table => 2,
            CompletionKind.Function => 1,
            _ => 0,
        };

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
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

        // A file on disk is linked, so saving it in another editor updates the answer here.
        if (files[0].TryGetLocalPath() is { } path)
        {
            workspace.OpenFile(path);
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        workspace.ReplaceAnswer(await reader.ReadToEndAsync());
    }
}
