using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Helpers;
using Hexoer.Models;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class ContentViewModel : PageViewModelBase, IDisposable
{
    private readonly ServiceHost _services;
    private bool _loading;
    private bool _syncingFrontMatter;
    private List<ContentItem> _all = [];
    private CancellationTokenSource? _previewCts;
    private readonly IdleAutoSave _autoSave;

    public override string Title => "文章";
    public override string Icon => "📝";

    public ContentViewModel(ServiceHost services)
    {
        _services = services;
        _autoSave = new IdleAutoSave(
            () => IsDirty && SelectedFile is { IsDirectory: false },
            () => SaveCoreAsync(refreshList: false, auto: true));
        var saved = _services.Project.Settings.MarkdownEditorMode;
        if (saved.Equals("Source", StringComparison.OrdinalIgnoreCase))
            EditorMode = MarkdownEditorMode.Source;
        else
            RefreshEditorModePresentation();
        AlignCorrespondingPreview();
        RefreshPreviewPresentation();
    }

    public ObservableCollection<ContentItem> Files { get; } = [];

    public IReadOnlyList<string> SortOptions { get; } =
    [
        "文章日期（新到舊）",
        "文章日期（舊到新）",
        "名稱（A–Z）",
        "名稱（Z–A）"
    ];

    public IReadOnlyList<string> StatusOptions { get; } =
    [
        "全部文章",
        "已發布",
        "草稿",
        "未設定日期"
    ];

    [ObservableProperty] public partial ContentItem? SelectedFile { get; set; }
    [ObservableProperty] public partial string EditorText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewMarkdown { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreviewBodyMarkdown { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasPreviewBody { get; set; }
    [ObservableProperty] public partial bool ShowPreview { get; set; } = true;
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial string NewPostTitle { get; set; } = ArticleCode.Format(DateTimeOffset.Now, 1);
    [ObservableProperty] public partial string NewPostFolder { get; set; } = "_posts";
    [ObservableProperty] public partial string NextArticleCode { get; set; } = ArticleCode.Format(DateTimeOffset.Now, 1);
    [ObservableProperty] public partial string Filter { get; set; } = string.Empty;
    [ObservableProperty] public partial string SortMode { get; set; } = "文章日期（新到舊）";
    [ObservableProperty] public partial string StatusFilter { get; set; } = "全部文章";
    [ObservableProperty] public partial string FileCountLabel { get; set; } = "0 篇";
    [ObservableProperty] public partial string ContentSummary { get; set; } = "尚未載入文章";
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial bool HasVisibleFiles { get; set; }
    [ObservableProperty] public partial bool ShowEmptyState { get; set; } = true;
    [ObservableProperty] public partial bool HasActiveFilters { get; set; }
    [ObservableProperty] public partial string EmptyStateTitle { get; set; } = "尚無文章";
    [ObservableProperty] public partial string EmptyStateDescription { get; set; } = "建立第一篇文章後，會顯示在這裡。";
    [ObservableProperty] public partial string FrontMatterTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string FrontMatterDate { get; set; } = string.Empty;
    [ObservableProperty] public partial string FrontMatterSlug { get; set; } = string.Empty;
    [ObservableProperty] public partial string FrontMatterCategories { get; set; } = string.Empty;
    [ObservableProperty] public partial string FrontMatterTags { get; set; } = string.Empty;
    [ObservableProperty] public partial string FrontMatterImage { get; set; } = string.Empty;
    [ObservableProperty] public partial string FrontMatterDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsDraft { get; set; }
    [ObservableProperty] public partial string PreviewModeLabel { get; set; } = "即時預覽：開";
    [ObservableProperty] public partial string EditorStatistics { get; set; } = "正文 0 字元 · 1 行";
    [ObservableProperty] public partial MarkdownEditorMode EditorMode { get; set; } = MarkdownEditorMode.Wysiwyg;
    [ObservableProperty] public partial bool IsWysiwygMode { get; set; } = true;
    [ObservableProperty] public partial bool IsSourceMode { get; set; }
    [ObservableProperty] public partial string EditorModeTitle { get; set; } = "WYSIWYG 視覺編輯";
    [ObservableProperty] public partial MarkdownPreviewKind PreviewKind { get; set; } = MarkdownPreviewKind.MarkdownOutput;
    [ObservableProperty] public partial bool IsRenderPreview { get; set; }
    [ObservableProperty] public partial bool IsMarkdownOutputPreview { get; set; } = true;
    [ObservableProperty] public partial string PreviewCorrespondenceHint { get; set; } = "對應 WYSIWYG：產生的 Markdown 原文";
    [ObservableProperty] public partial string PreviewKindTitle { get; set; } = "Markdown 輸出";

    public override void OnNavigatedTo() => Refresh();

    partial void OnSelectedFileChanging(ContentItem? oldValue, ContentItem? newValue)
    {
        _autoSave.Cancel();
        if (_loading || !IsDirty || oldValue is null || oldValue.IsDirectory)
            return;

        var path = oldValue.FullPath;
        var text = EditorText;
        IsDirty = false;
        _ = PersistSilentlyAsync(path, text);
    }

    partial void OnSelectedFileChanged(ContentItem? value)
    {
        HasSelection = value is not null && !value.IsDirectory;
        if (value is not null && !value.IsDirectory)
            _ = LoadFileAsync(value);
    }

    partial void OnEditorTextChanged(string value)
    {
        UpdateEditorStatistics(value);
        UpdatePreviewBody(value);
        if (!_loading)
            MarkDirty();

        if (!_syncingFrontMatter)
            PopulateFrontMatter(value);

        SchedulePreviewUpdate(value);
    }

    private void UpdatePreviewBody(string markdown)
    {
        PreviewBodyMarkdown = MarkdownPreviewService.StripFrontMatter(markdown ?? string.Empty);
        HasPreviewBody = !string.IsNullOrWhiteSpace(PreviewBodyMarkdown);
    }

    private void UpdateEditorStatistics(string markdown)
    {
        var body = MarkdownPreviewService.StripFrontMatter(markdown);
        var characters = body.Count(character => !char.IsWhiteSpace(character));
        var lines = string.IsNullOrEmpty(body) ? 1 : body.Count(character => character == '\n') + 1;
        EditorStatistics = $"正文 {characters:N0} 字元 · {lines:N0} 行";
    }

    partial void OnFrontMatterTitleChanged(string value) => UpdateEditorFromFrontMatter();
    partial void OnFrontMatterDateChanged(string value) => UpdateEditorFromFrontMatter();
    partial void OnFrontMatterSlugChanged(string value) => UpdateEditorFromFrontMatter();
    partial void OnFrontMatterCategoriesChanged(string value) => UpdateEditorFromFrontMatter();
    partial void OnFrontMatterTagsChanged(string value) => UpdateEditorFromFrontMatter();
    partial void OnFrontMatterImageChanged(string value) => UpdateEditorFromFrontMatter();
    partial void OnFrontMatterDescriptionChanged(string value) => UpdateEditorFromFrontMatter();
    partial void OnIsDraftChanged(bool value) => UpdateEditorFromFrontMatter();

    partial void OnShowPreviewChanged(bool value)
    {
        PreviewModeLabel = value ? "即時預覽：開" : "即時預覽：關";
        if (value)
            SchedulePreviewUpdate(EditorText);
    }

    partial void OnEditorModeChanged(MarkdownEditorMode value)
    {
        RefreshEditorModePresentation();
        AlignCorrespondingPreview();
        _services.Project.Settings.SetMarkdownEditorMode(value == MarkdownEditorMode.Source ? "Source" : "Wysiwyg");
    }

    partial void OnPreviewKindChanged(MarkdownPreviewKind value) => RefreshPreviewPresentation();

    private void RefreshEditorModePresentation()
    {
        IsWysiwygMode = EditorMode == MarkdownEditorMode.Wysiwyg;
        IsSourceMode = EditorMode == MarkdownEditorMode.Source;
        EditorModeTitle = IsWysiwygMode ? "WYSIWYG 視覺編輯" : "Markdown 原始碼";
        RefreshPreviewPresentation();
    }

    private void AlignCorrespondingPreview()
    {
        var corresponding = EditorMode == MarkdownEditorMode.Source
            ? MarkdownPreviewKind.Render
            : MarkdownPreviewKind.MarkdownOutput;
        if (PreviewKind != corresponding)
            PreviewKind = corresponding;
        else
            RefreshPreviewPresentation();
    }

    private void RefreshPreviewPresentation()
    {
        IsRenderPreview = PreviewKind == MarkdownPreviewKind.Render;
        IsMarkdownOutputPreview = PreviewKind == MarkdownPreviewKind.MarkdownOutput;
        PreviewKindTitle = IsRenderPreview ? "渲染預覽" : "Markdown 輸出";
        PreviewCorrespondenceHint = (EditorMode, PreviewKind) switch
        {
            (MarkdownEditorMode.Wysiwyg, MarkdownPreviewKind.Render) =>
                "對應 WYSIWYG：Markdig 渲染結果",
            (MarkdownEditorMode.Wysiwyg, MarkdownPreviewKind.MarkdownOutput) =>
                "對應 WYSIWYG：產生的 Markdown 原文",
            (MarkdownEditorMode.Source, MarkdownPreviewKind.Render) =>
                "對應原始碼：即時渲染預覽",
            _ =>
                "對應原始碼：目前 Markdown 正文"
        };
    }

    [RelayCommand]
    private void SetWysiwygMode() => EditorMode = MarkdownEditorMode.Wysiwyg;

    [RelayCommand]
    private void SetSourceMode() => EditorMode = MarkdownEditorMode.Source;

    [RelayCommand]
    private void SetRenderPreview() => PreviewKind = MarkdownPreviewKind.Render;

    [RelayCommand]
    private void SetMarkdownOutputPreview() => PreviewKind = MarkdownPreviewKind.MarkdownOutput;

    [RelayCommand]
    private void ToggleEditorMode() =>
        EditorMode = EditorMode == MarkdownEditorMode.Wysiwyg
            ? MarkdownEditorMode.Source
            : MarkdownEditorMode.Wysiwyg;

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSortModeChanged(string value) => ApplyFilter();
    partial void OnStatusFilterChanged(string value) => ApplyFilter();

    private void SchedulePreviewUpdate(string value)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, token);
                if (token.IsCancellationRequested) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        PreviewMarkdown = value;
                });
            }
            catch (TaskCanceledException)
            {
            }
        }, token);
    }

    [RelayCommand]
    private void TogglePreview() => ShowPreview = !ShowPreview;

    [RelayCommand]
    private void ClearFilters()
    {
        Filter = string.Empty;
        StatusFilter = "全部文章";
    }

    [RelayCommand]
    private void Refresh()
    {
        var selectedPath = SelectedFile?.FullPath;
        Files.Clear();
        if (!RequireProject(out _))
        {
            _all = [];
            ApplyFilter();
            return;
        }

        _all = _services.Content.ListArticles().ToList();
        ApplyFilter();
        if (selectedPath is not null)
            SelectedFile = Files.FirstOrDefault(item =>
                item.FullPath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        StatusMessage = $"共 {_all.Count} 篇 Markdown";
    }

    private void ApplyFilter()
    {
        Files.Clear();
        IEnumerable<ContentItem> q = _all;
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            q = q.Where(f =>
                f.RelativePath.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || f.DisplayTitle.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        }

        q = StatusFilter switch
        {
            "已發布" => q.Where(item => item.IsPublished),
            "草稿" => q.Where(item => item.IsDraft),
            "未設定日期" => q.Where(item => !item.HasArticleDate),
            _ => q
        };

        q = SortMode switch
        {
            "文章日期（舊到新）" => q.OrderByDescending(item => item.ArticleDate.HasValue)
                .ThenBy(item => item.ArticleDate)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "名稱（A–Z）" => q.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            "名稱（Z–A）" => q.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderByDescending(item => item.ArticleDate.HasValue)
                .ThenByDescending(item => item.ArticleDate)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var item in q)
            Files.Add(item);

        var publishedCount = _all.Count(item => item.IsPublished);
        var draftCount = _all.Count(item => item.IsDraft);
        var missingDateCount = _all.Count(item => !item.HasArticleDate);
        ContentSummary = $"{_all.Count} 篇文章 · {publishedCount} 已發布 · {draftCount} 草稿 · {missingDateCount} 缺少日期";

        HasActiveFilters = !string.IsNullOrWhiteSpace(Filter) || StatusFilter != "全部文章";
        FileCountLabel = !HasActiveFilters
            ? $"{Files.Count} 篇"
            : $"顯示 {Files.Count} / {_all.Count} 篇";
        HasVisibleFiles = Files.Count > 0;
        ShowEmptyState = !HasVisibleFiles;
        EmptyStateTitle = _all.Count == 0 ? "尚無文章" : "找不到符合條件的文章";
        EmptyStateDescription = _all.Count == 0
            ? "按「新增文章」會用 hexo new 建立 source/_posts 檔案。關於、歸檔等請到「選單」分頁。"
            : "調整搜尋文字或狀態篩選後再試一次。";

        RefreshSuggestedArticleCode();

        if (SelectedFile is not null && !Files.Contains(SelectedFile))
            SelectedFile = null;
    }

    private async Task LoadFileAsync(ContentItem item)
    {
        _autoSave.Cancel();
        _loading = true;
        try
        {
            EditorText = await _services.Content.ReadAsync(item.FullPath);
            PreviewMarkdown = EditorText;
            UpdatePreviewBody(EditorText);
            PopulateFrontMatter(EditorText);
            IsDirty = false;
            StatusMessage = item.RelativePath;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(refreshList: true, auto: false);

    private void MarkDirty()
    {
        IsDirty = true;
        _autoSave.Schedule();
    }

    private async Task SaveCoreAsync(bool refreshList, bool auto)
    {
        if (SelectedFile is null || SelectedFile.IsDirectory)
        {
            if (!auto)
                StatusMessage = "請先選擇檔案";
            return;
        }

        try
        {
            await _services.Content.SaveAsync(SelectedFile.FullPath, EditorText);
            IsDirty = false;
            _autoSave.Cancel();
            StatusMessage = auto
                ? $"已自動儲存：{SelectedFile.RelativePath}"
                : $"已儲存：{SelectedFile.RelativePath}";
            if (refreshList)
                Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = auto ? $"自動儲存失敗：{ex.Message}" : ex.Message;
        }
    }

    private async Task PersistSilentlyAsync(string fullPath, string text)
    {
        try
        {
            await _services.Content.SaveAsync(fullPath, text);
            StatusMessage = $"已自動儲存：{Path.GetFileName(fullPath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"自動儲存失敗：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreatePostAsync()
    {
        if (!RequireProject(out _)) return;

        var asDraft = NewPostFolder.Contains("draft", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(NewPostTitle) ? NextArticleCode : NewPostTitle.Trim();

        try
        {
            var path = await _services.Content.CreatePostAsync(title, asDraft);
            StatusMessage = asDraft
                ? $"已建立草稿：{Path.GetFileName(path)}"
                : $"已建立：{Path.GetFileName(path)}";
            Refresh();
            SelectedFile = _all.FirstOrDefault(item =>
                item.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedFile is null) return;
        try
        {
            _services.Content.Delete(SelectedFile.FullPath);
            StatusMessage = $"已刪除：{SelectedFile.RelativePath}";
            SelectedFile = null;
            EditorText = string.Empty;
            PreviewMarkdown = string.Empty;
            UpdatePreviewBody(string.Empty);
            Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task UploadCoverImageAsync()
    {
        if (!RequireProject(out var site)) return;
        if (!HasSelection)
        {
            StatusMessage = "請先選擇文章";
            return;
        }

        var path = await DialogHelper.PickFileAsync("選擇封面圖片", [DialogHelper.Images]);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var asset = MediaAssetService.Import(site, path, MediaKind.Image);
            FrontMatterImage = asset.PublicUrl;
            StatusMessage = $"封面已上傳至 source/{asset.Folder}/{Path.GetFileName(asset.DestinationPath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenHtmlPreviewAsync()
    {
        try
        {
            var html = MarkdownPreviewService.ToHtmlDocument(
                EditorText,
                SelectedFile?.Name ?? "preview");
            html = MediaAssetService.ToPreviewHtml(html, _services.CurrentSitePath);
            var dir = Path.Combine(Path.GetTempPath(), "HexoerPreview");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "preview.html");
            await File.WriteAllTextAsync(path, html);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            StatusMessage = "已在瀏覽器開啟 HTML 預覽";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnNewPostFolderChanged(string value) => RefreshSuggestedArticleCode();

    private void RefreshSuggestedArticleCode()
    {
        var folder = NewPostFolder.Contains("draft", StringComparison.OrdinalIgnoreCase)
            ? _services.Project.DraftsDir
            : _services.Project.PostsDir;
        var previous = NextArticleCode;
        NextArticleCode = string.IsNullOrWhiteSpace(_services.CurrentSitePath)
            ? ArticleCode.Format(DateTimeOffset.Now, 1)
            : ArticleCode.NextInDirectory(folder, DateTimeOffset.Now);
        if (string.IsNullOrWhiteSpace(NewPostTitle)
            || NewPostTitle == previous
            || ArticleCodeLooksLikeDefault(NewPostTitle))
            NewPostTitle = NextArticleCode;
    }

    private static bool ArticleCodeLooksLikeDefault(string value)
    {
        var text = value.Trim();
        return text.Length >= 10
               && text[8] == '-'
               && text.Take(8).All(char.IsDigit)
               && text.Skip(9).All(char.IsDigit);
    }

    private void PopulateFrontMatter(string text)
    {
        var document = _services.FrontMatter.Parse(text);
        _syncingFrontMatter = true;
        try
        {
            FrontMatterTitle = GetField(document, "title");
            FrontMatterDate = GetField(document, "date");
            FrontMatterSlug = GetField(document, "slug");
            FrontMatterCategories = GetField(document, "categories");
            FrontMatterTags = GetField(document, "tags");
            FrontMatterImage = FirstField(document, "image", "cover", "photos");
            FrontMatterDescription = GetField(document, "description");
            IsDraft = SelectedFile?.IsDraft == true
                      || (document.Fields.TryGetValue("published", out var published)
                          && published.Equals("false", StringComparison.OrdinalIgnoreCase))
                      || (document.Fields.TryGetValue("draft", out var draft)
                          && draft.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncingFrontMatter = false;
        }
    }

    private void UpdateEditorFromFrontMatter()
    {
        if (_loading || _syncingFrontMatter) return;

        var document = _services.FrontMatter.Parse(EditorText);
        SetField(document, "title", FrontMatterTitle);
        SetField(document, "date", FrontMatterDate);
        SetField(document, "slug", FrontMatterSlug);
        SetField(document, "categories", FrontMatterCategories);
        SetField(document, "tags", FrontMatterTags);
        SetField(document, "image", FrontMatterImage);
        if (document.Fields.ContainsKey("cover"))
            SetField(document, "cover", FrontMatterImage);
        SetField(document, "description", FrontMatterDescription);
        document.Fields["published"] = IsDraft ? "false" : "true";
        if (document.Fields.ContainsKey("draft"))
            document.Fields["draft"] = IsDraft ? "true" : "false";

        _syncingFrontMatter = true;
        try
        {
            EditorText = _services.FrontMatter.Write(document);
        }
        finally
        {
            _syncingFrontMatter = false;
        }
    }

    private static string GetField(FrontMatterDocument document, string key) =>
        document.Fields.TryGetValue(key, out var value) ? value : string.Empty;

    private static string FirstField(FrontMatterDocument document, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetField(document, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static void SetField(FrontMatterDocument document, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            document.Fields.Remove(key);
        else
            document.Fields[key] = value.Trim();
    }

    public void Dispose()
    {
        _autoSave.Cancel();
        if (IsDirty && SelectedFile is { IsDirectory: false } file)
        {
            try
            {
                File.WriteAllText(file.FullPath, EditorText);
            }
            catch
            {
            }
        }

        _autoSave.Dispose();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
    }
}
