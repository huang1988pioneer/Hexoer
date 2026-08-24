using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using Hexoer.Helpers;
using Hexoer.Models;
using Hexoer.Services;
using Hexoer.ViewModels;
using TextMateSharp.Grammars;

namespace Hexoer.Views;

public partial class ContentView : UserControl
{
    /// <summary>Matches a Markdown list / quote prefix at line start ("  - [ ] ", "3. ", "> ").</summary>
    private static readonly Regex ListPrefixRegex = new(
        @"^(?<indent>\s*)(?:(?<bullet>[-*])\s(?<task>\[[ xX]\]\s)?|(?<num>\d{1,9})\.\s|(?<quote>>)\s)",
        RegexOptions.Compiled);

    private bool _syncingFromDocument;
    private bool _syncingFromViewModel;
    private bool _wysiwygHooked;

    public ContentView()
    {
        InitializeComponent();
        ConfigureEditor();
        DragDrop.SetAllowDrop(EditorCard, true);
        EditorCard.AddHandler(DragDrop.DragOverEvent, Editor_OnDragOver);
        EditorCard.AddHandler(DragDrop.DropEvent, Editor_OnDrop);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != DataContextProperty) return;
        if (e.OldValue is ContentViewModel oldViewModel)
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is ContentViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncFromViewModel(newViewModel.EditorText);
            SyncWysiwygFromViewModel(newViewModel.EditorText);
            ApplyEditorMode(newViewModel.EditorMode, flush: false);
            ApplyPreviewPane(newViewModel.ShowPreview);
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (!_wysiwygHooked)
        {
            WysiwygEditor.MarkdownChanged += Wysiwyg_OnMarkdownChanged;
            WysiwygEditor.SaveRequested += Wysiwyg_OnSaveRequested;
            WysiwygEditor.ToggleModeRequested += Wysiwyg_OnToggleModeRequested;
            WysiwygEditor.EditorFailed += Wysiwyg_OnEditorFailed;
            _wysiwygHooked = true;
        }

        if (DataContext is ContentViewModel viewModel)
        {
            SyncWysiwygFromViewModel(viewModel.EditorText);
            ApplyPreviewPane(viewModel.ShowPreview);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_wysiwygHooked)
        {
            WysiwygEditor.MarkdownChanged -= Wysiwyg_OnMarkdownChanged;
            WysiwygEditor.SaveRequested -= Wysiwyg_OnSaveRequested;
            WysiwygEditor.ToggleModeRequested -= Wysiwyg_OnToggleModeRequested;
            WysiwygEditor.EditorFailed -= Wysiwyg_OnEditorFailed;
            _wysiwygHooked = false;
        }

        base.OnUnloaded(e);
    }

    private void ConfigureEditor()
    {
        MarkdownEditor.Options.ConvertTabsToSpaces = true;
        MarkdownEditor.Options.IndentationSize = 2;
        MarkdownEditor.Options.HighlightCurrentLine = true;

        InstallMarkdownHighlighting();

        MarkdownEditor.Document.TextChanged += (_, _) => SyncToViewModel();

        // Tunnel strategy: run before TextArea's built-in key handling.
        MarkdownEditor.AddHandler(KeyDownEvent, EditorKeyDown, RoutingStrategies.Tunnel);
    }

    private void InstallMarkdownHighlighting()
    {
        var registryOptions = new RegistryOptions(ThemeName.Abbys);
        var installation = MarkdownEditor.InstallTextMate(registryOptions);
        var language = registryOptions.GetLanguageByExtension(".md");
        installation.SetGrammar(registryOptions.GetScopeByLanguageId(language.Id));
    }

    private bool IsWysiwyg => DataContext is ContentViewModel { IsWysiwygMode: true };

    private void Undo_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsWysiwyg)
        {
            _ = WysiwygEditor.ExecAsync("undo");
            return;
        }

        if (MarkdownEditor.CanUndo) MarkdownEditor.Undo();
        MarkdownEditor.Focus();
    }

    private void Redo_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsWysiwyg)
        {
            _ = WysiwygEditor.ExecAsync("redo");
            return;
        }

        if (MarkdownEditor.CanRedo) MarkdownEditor.Redo();
        MarkdownEditor.Focus();
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        if (IsWysiwyg)
            await WysiwygEditor.FlushAsync();
        if (DataContext is ContentViewModel viewModel && viewModel.SaveCommand.CanExecute(null))
            viewModel.SaveCommand.Execute(null);
    }

    private void ToggleWrap_OnClick(object? sender, RoutedEventArgs e)
    {
        MarkdownEditor.WordWrap = !MarkdownEditor.WordWrap;
        if (sender is Button button) button.Content = MarkdownEditor.WordWrap ? "換行" : "不換行";
    }

    private void Bold_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("bold", (text, start, length) =>
            MarkdownEditingService.Wrap(text, start, length, "**", "**", "粗體文字"));

    private void Italic_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("italic", (text, start, length) =>
            MarkdownEditingService.Wrap(text, start, length, "*", "*", "斜體文字"));

    private void Strike_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("strike", (text, start, length) =>
            MarkdownEditingService.Wrap(text, start, length, "~~", "~~", "刪除文字"));

    private void InlineCode_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("inlineCode", (text, start, length) =>
            MarkdownEditingService.Wrap(text, start, length, "`", "`", "程式碼"));

    private void Link_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("link", (text, start, length) =>
            MarkdownEditingService.Link(text, start, length, image: false));

    private void Image_OnClick(object? sender, RoutedEventArgs e) =>
        _ = UploadMediaAsync(MediaKind.Image, "選擇圖片", [DialogHelper.Images]);

    private void Music_OnClick(object? sender, RoutedEventArgs e) =>
        _ = UploadMediaAsync(MediaKind.Music, "選擇音樂", [DialogHelper.Audio]);

    private void Voice_OnClick(object? sender, RoutedEventArgs e) =>
        _ = UploadMediaAsync(MediaKind.Voice, "選擇語音", [DialogHelper.Audio]);

    private void Video_OnClick(object? sender, RoutedEventArgs e) =>
        _ = UploadMediaAsync(MediaKind.Video, "選擇影片", [DialogHelper.Videos]);

    private void Pdf_OnClick(object? sender, RoutedEventArgs e) =>
        _ = UploadMediaAsync(MediaKind.Pdf, "選擇 PDF", [DialogHelper.Pdf]);

    private void OtherFile_OnClick(object? sender, RoutedEventArgs e) =>
        _ = UploadMediaAsync(null, "選擇檔案", [DialogHelper.Images, DialogHelper.Audio, DialogHelper.Videos, DialogHelper.Pdf, DialogHelper.AllFiles]);

    private void Editor_OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Editor_OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ContentViewModel viewModel) return;
        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;

        var paths = items
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .ToList();
        await ImportAndInsertAsync(viewModel, paths, forcedKind: null);
    }

    private async Task UploadMediaAsync(
        MediaKind? kind,
        string title,
        IReadOnlyList<FilePickerFileType> types)
    {
        if (DataContext is not ContentViewModel viewModel) return;
        if (string.IsNullOrWhiteSpace(AppServices.Instance.CurrentSitePath))
        {
            viewModel.StatusMessage = "請先在「環境設定」開啟、建立，或從遠端 Git 複製 Hexo 專案。";
            return;
        }

        var paths = await DialogHelper.PickFilesAsync(title, types);
        await ImportAndInsertAsync(viewModel, paths, kind);
    }

    private async Task ImportAndInsertAsync(
        ContentViewModel viewModel,
        IReadOnlyList<string> paths,
        MediaKind? forcedKind)
    {
        if (paths.Count == 0) return;
        var site = AppServices.Instance.CurrentSitePath;
        if (string.IsNullOrWhiteSpace(site))
        {
            viewModel.StatusMessage = "請先在「環境設定」開啟、建立，或從遠端 Git 複製 Hexo 專案。";
            return;
        }

        try
        {
            var assets = MediaAssetService.ImportMany(site, paths, forcedKind);
            if (assets.Count == 0)
            {
                viewModel.StatusMessage = "沒有可上傳的檔案。";
                return;
            }

            if (viewModel.HasSelection)
            {
                if (IsWysiwyg && WysiwygEditor.IsReady)
                {
                    await WysiwygEditor.AddMediaAsync(MediaAssetService.MediaMapForAssets(assets));
                    await WysiwygEditor.ExecAsync("insertHtml", MediaAssetService.JoinPreviewHtml(assets));
                }
                else
                    ApplyEdit((text, start, length) =>
                        MarkdownEditingService.InsertSnippet(
                            text,
                            start,
                            length,
                            MediaAssetService.JoinMarkdown(assets)));
            }

            var folders = string.Join("、", assets.Select(asset => asset.Folder).Distinct());
            var inserted = viewModel.HasSelection ? "，並插入文章" : "。開啟文章後可用工具列再插入";
            viewModel.StatusMessage = assets.Count == 1
                ? $"已上傳至 source/{assets[0].Folder}/{Path.GetFileName(assets[0].DestinationPath)}{inserted}"
                : $"已上傳 {assets.Count} 個檔案至 source/{folders}{inserted}";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = ex.Message;
        }
    }

    private void Heading1_OnClick(object? sender, RoutedEventArgs e) => ApplyHeading(1);
    private void Heading2_OnClick(object? sender, RoutedEventArgs e) => ApplyHeading(2);
    private void Heading3_OnClick(object? sender, RoutedEventArgs e) => ApplyHeading(3);

    private void ApplyHeading(int level)
    {
        if (IsWysiwyg)
        {
            _ = WysiwygEditor.ExecAsync("heading", level.ToString());
            return;
        }

        ApplyEdit((text, start, length) => MarkdownEditingService.Heading(text, start, length, level));
    }

    private void Quote_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("quote", (text, start, length) =>
            MarkdownEditingService.PrefixLines(text, start, length, "> "));

    private void BulletList_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("bulletList", (text, start, length) =>
            MarkdownEditingService.PrefixLines(text, start, length, "- "));

    private void TaskList_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("taskList", (text, start, length) =>
            MarkdownEditingService.PrefixLines(text, start, length, "- [ ] "));

    private void OrderedList_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("orderedList", MarkdownEditingService.OrderedList);

    private void CodeBlock_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("codeBlock", MarkdownEditingService.CodeBlock);

    private void Table_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("table", MarkdownEditingService.InsertTable);

    private void HorizontalRule_OnClick(object? sender, RoutedEventArgs e) =>
        RunEditorCommand("hr", MarkdownEditingService.HorizontalRule);

    private void RunEditorCommand(string wysiwygCommand, Func<string, int, int, MarkdownEditResult> sourceOperation)
    {
        if (IsWysiwyg)
        {
            _ = WysiwygEditor.ExecAsync(wysiwygCommand);
            return;
        }

        ApplyEdit(sourceOperation);
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.B:
                    Bold_OnClick(sender, e);
                    e.Handled = true;
                    return;
                case Key.I:
                    Italic_OnClick(sender, e);
                    e.Handled = true;
                    return;
                case Key.K:
                    Link_OnClick(sender, e);
                    e.Handled = true;
                    return;
                case Key.S when DataContext is ContentViewModel viewModel:
                    if (viewModel.SaveCommand.CanExecute(null))
                        viewModel.SaveCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.M when e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                                && DataContext is ContentViewModel modeViewModel:
                    modeViewModel.ToggleEditorModeCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            ContinueListOnEnter(e);
    }

    /// <summary>
    /// MarkText-style list continuation: pressing Enter keeps the current list marker,
    /// increments ordered numbers, and collapses an empty item instead of nesting empties.
    /// </summary>
    private void ContinueListOnEnter(KeyEventArgs e)
    {
        var document = MarkdownEditor.Document;
        var caret = MarkdownEditor.CaretOffset;
        var line = document.GetLineByOffset(caret);
        var beforeCaret = document.GetText(line.Offset, caret - line.Offset);
        var match = ListPrefixRegex.Match(beforeCaret);
        if (!match.Success) return;

        var indent = match.Groups["indent"].Value;
        var afterMarker = beforeCaret[match.Length..];

        if (string.IsNullOrWhiteSpace(afterMarker))
        {
            // Empty item: strip the marker so the new line exits the list.
            document.Remove(line.Offset, match.Length);
            return;
        }

        string marker;
        if (match.Groups["num"].Success)
            marker = $"{int.Parse(match.Groups["num"].Value) + 1}. ";
        else if (match.Groups["task"].Success)
            marker = $"{match.Groups["bullet"].Value} [ ] ";
        else if (match.Groups["bullet"].Success)
            marker = $"{match.Groups["bullet"].Value} ";
        else
            marker = "> ";

        var insertion = "\n" + indent + marker;
        document.Insert(caret, insertion);
        MarkdownEditor.CaretOffset = caret + insertion.Length;
        e.Handled = true;
    }

    private void ApplyEdit(Func<string, int, int, MarkdownEditResult> operation)
    {
        var text = GetText();
        var start = MarkdownEditor.SelectionLength > 0
            ? MarkdownEditor.SelectionStart
            : Math.Min(MarkdownEditor.CaretOffset, text.Length);
        var length = MarkdownEditor.SelectionLength;

        if (MarkdownEditingService.IsInsideFrontMatter(text, start))
        {
            if (DataContext is ContentViewModel viewModel)
                viewModel.StatusMessage = "先使用上方欄位編輯 front matter；格式工具只作用於 Markdown 正文。";
            return;
        }

        var result = operation(text, start, length);
        ReplaceDocument(result.Text);
        Dispatcher.UIThread.Post(() =>
        {
            MarkdownEditor.Focus();
            var safeStart = Math.Clamp(result.SelectionStart, 0, GetText().Length);
            var safeLength = Math.Clamp(result.SelectionLength, 0, GetText().Length - safeStart);
            MarkdownEditor.Select(safeStart, safeLength);
        });
    }

    private void ReplaceDocument(string newText)
    {
        var document = MarkdownEditor.Document;
        using (document.RunUpdate())
        {
            document.Replace(0, document.TextLength, newText);
        }
    }

    private void SyncToViewModel()
    {
        if (_syncingFromViewModel || DataContext is not ContentViewModel viewModel) return;
        _syncingFromDocument = true;
        try
        {
            viewModel.EditorText = GetText();
        }
        finally
        {
            _syncingFromDocument = false;
        }
    }

    private void SyncFromViewModel(string text)
    {
        if (_syncingFromDocument) return;
        var document = MarkdownEditor.Document;
        if (document.Text == text) return;
        _syncingFromViewModel = true;
        try
        {
            document.Text = text ?? string.Empty;
        }
        finally
        {
            _syncingFromViewModel = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ContentViewModel viewModel) return;
        if (e.PropertyName == nameof(ContentViewModel.EditorText))
        {
            SyncFromViewModel(viewModel.EditorText);
            SyncWysiwygFromViewModel(viewModel.EditorText);
        }
        else if (e.PropertyName == nameof(ContentViewModel.EditorMode))
        {
            _ = ApplyEditorModeAsync(viewModel.EditorMode);
        }
        else if (e.PropertyName == nameof(ContentViewModel.ShowPreview))
        {
            ApplyPreviewPane(viewModel.ShowPreview);
        }
        else if (e.PropertyName == nameof(ContentViewModel.PreviewKind)
                 && viewModel.IsWysiwygMode)
        {
            _ = WysiwygEditor.FlushAsync();
        }
    }

    private void ApplyPreviewPane(bool show)
    {
        if (EditorPreviewGrid is null || EditorPreviewGrid.ColumnDefinitions.Count < 3)
            return;
        EditorPreviewGrid.ColumnDefinitions[2].Width = show
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private void ApplyEditorMode(MarkdownEditorMode mode, bool flush) =>
        _ = ApplyEditorModeAsync(mode, flush);

    private async Task ApplyEditorModeAsync(MarkdownEditorMode mode, bool flush = true)
    {
        var source = mode == MarkdownEditorMode.Source;
        MarkdownEditor.ZIndex = source ? 1 : 0;
        WysiwygEditor.ZIndex = source ? 0 : 1;

        if (source)
        {
            if (flush)
                await WysiwygEditor.FlushAsync();
            MarkdownEditor.Focus();
            return;
        }

        if (DataContext is ContentViewModel viewModel)
            SyncWysiwygFromViewModel(viewModel.EditorText);
        await WysiwygEditor.FocusEditorAsync();
    }

    private void SyncWysiwygFromViewModel(string text)
    {
        if (_syncingFromDocument) return;
        WysiwygEditor.SitePath = AppServices.Instance.CurrentSitePath;
        var body = AppServices.Instance.FrontMatter.Parse(text ?? string.Empty).Body;
        if (string.Equals(WysiwygEditor.Markdown, body, StringComparison.Ordinal))
            return;
        WysiwygEditor.Markdown = body;
    }

    private void Wysiwyg_OnMarkdownChanged(object? sender, EventArgs e)
    {
        if (_syncingFromViewModel || DataContext is not ContentViewModel viewModel)
            return;

        _syncingFromDocument = true;
        try
        {
            viewModel.EditorText = AppServices.Instance.FrontMatter.ReplaceBody(
                viewModel.EditorText,
                WysiwygEditor.Markdown ?? string.Empty);
        }
        finally
        {
            _syncingFromDocument = false;
        }
    }

    private void Wysiwyg_OnSaveRequested(object? sender, EventArgs e)
    {
        if (DataContext is not ContentViewModel viewModel) return;
        if (viewModel.SaveCommand.CanExecute(null))
            viewModel.SaveCommand.Execute(null);
    }

    private void Wysiwyg_OnToggleModeRequested(object? sender, EventArgs e)
    {
        if (DataContext is ContentViewModel viewModel)
            viewModel.ToggleEditorModeCommand.Execute(null);
    }

    private void Wysiwyg_OnEditorFailed(object? sender, string e)
    {
        if (DataContext is not ContentViewModel viewModel) return;
        viewModel.EditorMode = MarkdownEditorMode.Source;
        viewModel.StatusMessage = "WYSIWYG 無法使用（需要 WebView2），已切換為原始碼模式。";
    }

    private string GetText() => MarkdownEditor.Document.Text ?? string.Empty;
}



