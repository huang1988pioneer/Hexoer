using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Helpers;
using Hexoer.Models;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class MenuEntryItem : ObservableObject
{
    public MenuEntryItem()
    {
    }

    public MenuEntryItem(MenuEntry entry)
    {
        MenuName = entry.MenuName;
        Identifier = entry.Identifier;
        Name = entry.Name;
        Url = entry.Url;
        PageRef = entry.PageRef;
        Parent = entry.Parent;
        Weight = entry.Weight;
        Icon = entry.Icon;
        NewTab = entry.NewTab;
        CameFromFrontMatter = entry.Source == MenuEntrySource.FrontMatter;
        FrontMatterPath = entry.SourcePath;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial string MenuName { get; set; } = "menu";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial string Identifier { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial string PageRef { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Parent { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial int Weight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial string Icon { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NewTab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayMeta))]
    public partial bool CameFromFrontMatter { get; set; }

    [ObservableProperty]
    public partial string? FrontMatterPath { get; set; }

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Name)
            ? (string.IsNullOrWhiteSpace(Identifier) ? "(未命名項目)" : Identifier)
            : Name;

    public string DisplayMeta
    {
        get
        {
            var target = !string.IsNullOrWhiteSpace(Url) ? Url : PageRef;
            if (string.IsNullOrWhiteSpace(target))
                target = "尚未設定網址";
            var icon = string.IsNullOrWhiteSpace(Icon) ? string.Empty : $" · {Icon}";
            var origin = CameFromFrontMatter ? " · 來自內容頁" : string.Empty;
            return $"{target} · 順序 {Weight}{icon}{origin}";
        }
    }

    public MenuEntry ToEntry() => new()
    {
        MenuName = string.IsNullOrWhiteSpace(MenuName) ? "menu" : MenuName.Trim(),
        Identifier = Identifier.Trim(),
        Name = Name.Trim(),
        Url = Url.Trim(),
        PageRef = PageRef.Trim(),
        Parent = Parent.Trim(),
        Weight = Weight,
        Icon = Icon.Trim(),
        NewTab = NewTab,
        Source = MenuEntrySource.Config
    };
}

public partial class MenuViewModel : PageViewModelBase, IDisposable
{
    private readonly ServiceHost _services;
    private readonly List<MenuEntryItem> _all = [];
    private SiteMenuDocument? _document;
    private bool _loading;
    private bool _syncingSelection;
    private readonly IdleAutoSave _autoSave;

    public override string Title => "選單";
    public override string Icon => "☰";

    public MenuViewModel(ServiceHost services)
    {
        _services = services;
        _autoSave = new IdleAutoSave(
            () => IsDirty || PageIsDirty,
            AutoSaveAsync);
    }

    public ObservableCollection<string> MenuNames { get; } = [];
    public ObservableCollection<MenuEntryItem> Entries { get; } = [];
    public ObservableCollection<ContentItem> SitePages { get; } = [];

    [ObservableProperty] public partial string SelectedMenuName { get; set; } = "menu";
    [ObservableProperty] public partial MenuEntryItem? SelectedEntry { get; set; }
    [ObservableProperty] public partial ContentItem? SelectedPage { get; set; }
    [ObservableProperty] public partial bool IsEditingMenu { get; set; }
    [ObservableProperty] public partial bool IsEditingPage { get; set; }
    [ObservableProperty] public partial bool HasMenuSelection { get; set; }
    [ObservableProperty] public partial bool HasPageSelection { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial bool ShowFrontMatterNotice { get; set; }
    [ObservableProperty] public partial string FrontMatterNotice { get; set; } = string.Empty;
    [ObservableProperty] public partial string MenuSummary { get; set; } = "尚未載入選單";
    [ObservableProperty] public partial string NewMenuName { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewPageTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewPageFolder { get; set; } = "about";
    [ObservableProperty] public partial string PageTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string PageBody { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PageIsDirty { get; set; }
    [ObservableProperty] public partial string ConfigPathLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowEmptyEditor { get; set; } = true;

    public override void OnNavigatedTo() => Refresh();

    partial void OnSelectedMenuNameChanged(string value)
    {
        if (_loading) return;
        RebuildVisibleEntries(preserveSelection: false);
    }

    partial void OnSelectedEntryChanged(MenuEntryItem? value)
    {
        HasMenuSelection = value is not null;
        if (_syncingSelection) return;
        if (value is null)
        {
            if (!HasPageSelection)
                IsEditingMenu = false;
            UpdateEditorMode();
            return;
        }

        _syncingSelection = true;
        SelectedPage = null;
        _syncingSelection = false;
        HasPageSelection = false;
        IsEditingMenu = true;
        IsEditingPage = false;
        UpdateEditorMode();
    }

    partial void OnSelectedPageChanged(ContentItem? value)
    {
        HasPageSelection = value is not null;
        if (_syncingSelection) return;
        if (value is null)
        {
            IsEditingPage = false;
            UpdateEditorMode();
            return;
        }

        _syncingSelection = true;
        SelectedEntry = null;
        _syncingSelection = false;
        HasMenuSelection = false;
        IsEditingMenu = false;
        IsEditingPage = true;
        UpdateEditorMode();
        _ = LoadPageAsync(value);
    }

    partial void OnPageTitleChanged(string value)
    {
        if (!_loading)
            MarkPageDirty();
    }

    partial void OnPageBodyChanged(string value)
    {
        if (!_loading)
            MarkPageDirty();
    }

    [RelayCommand]
    private void Refresh()
    {
        _autoSave.Cancel();
        _all.Clear();
        Entries.Clear();
        SitePages.Clear();
        MenuNames.Clear();
        SelectedEntry = null;
        SelectedPage = null;
        IsDirty = false;
        PageIsDirty = false;
        ShowFrontMatterNotice = false;
        FrontMatterNotice = string.Empty;
        _document = null;

        if (!RequireProject(out var site))
        {
            MenuSummary = "尚未選擇網站";
            ConfigPathLabel = string.Empty;
            return;
        }

        try
        {
            _loading = true;
            _document = _services.Menus.Load(site);
            foreach (var entry in _document.Entries.OrderBy(item => item.MenuName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Weight)
                         .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new MenuEntryItem(entry);
                item.PropertyChanged += OnEntryPropertyChanged;
                _all.Add(item);
            }

            foreach (var page in _services.Content.ListSitePages())
                SitePages.Add(page);

            RebuildMenuNames();
            if (!MenuNames.Contains(SelectedMenuName))
                SelectedMenuName = MenuNames.FirstOrDefault() ?? "menu";
            RebuildVisibleEntries(preserveSelection: false);

            ConfigPathLabel = _document.ConfigPath;
            MenuSummary = $"{_all.Count} 個選單項目 · {MenuNames.Count} 組選單 · {SitePages.Count} 個網站頁面";
            StatusMessage = $"已載入選單：{_document.ConfigPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            MenuSummary = "無法載入選單";
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void AddEntry()
    {
        EnsureMenuName();
        var item = new MenuEntryItem
        {
            MenuName = SelectedMenuName,
            Identifier = $"item-{DateTime.Now:HHmmss}",
            Name = "新項目",
            Url = "/",
            Weight = NextWeight()
        };
        item.PropertyChanged += OnEntryPropertyChanged;
        _all.Add(item);
        MarkDirty();
        RebuildMenuNames();
        RebuildVisibleEntries(preserveSelection: false);
        SelectedEntry = item;
        StatusMessage = "已新增選單項目（記得儲存）";
    }

    [RelayCommand]
    private void AddPreset(string? preset)
    {
        EnsureMenuName();
        var (identifier, name, url, icon) = (preset ?? string.Empty) switch
        {
            "歸檔 Archives" => ("archives", "Archives", "/archives/", "fa fa-archive"),
            "搜尋 Search" => ("search", "Search", "/search/", "fa fa-search"),
            "關於 About" => ("about", "About", "/about/", "fa fa-user"),
            _ => ("home", "Home", "/", "fa fa-home")
        };

        var existing = _all.FirstOrDefault(item =>
            item.MenuName.Equals(SelectedMenuName, StringComparison.OrdinalIgnoreCase)
            && (item.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                || NormalizeUrl(item.Url) == NormalizeUrl(url)));
        if (existing is not null)
        {
            SelectedEntry = existing;
            StatusMessage = $"選單已有「{existing.DisplayTitle}」";
            return;
        }

        var item = new MenuEntryItem
        {
            MenuName = SelectedMenuName,
            Identifier = identifier,
            Name = name,
            Url = url,
            Icon = icon,
            Weight = NextWeight()
        };
        item.PropertyChanged += OnEntryPropertyChanged;
        _all.Add(item);
        MarkDirty();
        RebuildVisibleEntries(preserveSelection: false);
        SelectedEntry = item;
        StatusMessage = $"已加入「{name}」（記得儲存）";
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedEntry is null) return;
        _all.Remove(SelectedEntry);
        MarkDirty();
        RebuildMenuNames();
        RebuildVisibleEntries(preserveSelection: false);
        StatusMessage = "已刪除選單項目（記得儲存）";
    }

    [RelayCommand]
    private void MoveUp() => MoveSelected(-1);

    [RelayCommand]
    private void MoveDown() => MoveSelected(1);

    [RelayCommand]
    private void AddMenu()
    {
        var name = string.IsNullOrWhiteSpace(NewMenuName) ? "social" : Slugify(NewMenuName);
        if (!MenuNames.Contains(name))
            MenuNames.Add(name);
        SelectedMenuName = name;
        NewMenuName = string.Empty;
        RebuildVisibleEntries(preserveSelection: false);
        StatusMessage = $"目前選單：{name}";
    }

    [RelayCommand]
    private void AddPageToMenu()
    {
        if (SelectedPage is null)
        {
            StatusMessage = "請先選擇網站頁面";
            return;
        }

        EnsureMenuName();
        var url = MenuService.UrlFromContentPath(SelectedPage.RelativePath);
        var identifier = GuessIdentifier(SelectedPage);
        var existing = _all.FirstOrDefault(item =>
            item.MenuName.Equals(SelectedMenuName, StringComparison.OrdinalIgnoreCase)
            && (item.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                || NormalizeUrl(item.Url) == NormalizeUrl(url)));
        if (existing is not null)
        {
            SelectedEntry = existing;
            StatusMessage = "該頁面已在目前選單中";
            return;
        }

        var item = new MenuEntryItem
        {
            MenuName = SelectedMenuName,
            Identifier = identifier,
            Name = string.IsNullOrWhiteSpace(SelectedPage.ArticleTitle)
                ? identifier
                : SelectedPage.ArticleTitle,
            Url = url,
            Icon = GuessIcon(identifier),
            Weight = NextWeight()
        };
        item.PropertyChanged += OnEntryPropertyChanged;
        _all.Add(item);
        MarkDirty();
        RebuildVisibleEntries(preserveSelection: false);
        SelectedEntry = item;
        StatusMessage = $"已將「{item.Name}」加入選單（記得儲存）";
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(reload: true, auto: false);

    [RelayCommand]
    private Task SavePageAsync() => SavePageCoreAsync(reload: true, auto: false);

    private void MarkDirty()
    {
        IsDirty = true;
        _autoSave.Schedule();
    }

    private void MarkPageDirty()
    {
        PageIsDirty = true;
        _autoSave.Schedule();
    }

    private async Task AutoSaveAsync()
    {
        if (IsDirty)
            await SaveCoreAsync(reload: false, auto: true);
        if (PageIsDirty)
            await SavePageCoreAsync(reload: false, auto: true);
    }

    private async Task SaveCoreAsync(bool reload, bool auto)
    {
        if (!RequireProject(out var site)) return;
        _document ??= _services.Menus.Load(site);

        try
        {
            var entries = _all.Select(item => item.ToEntry()).ToList();
            _services.Menus.Save(site, _document, entries);
            IsDirty = false;
            _autoSave.Cancel();
            if (PageIsDirty)
                _autoSave.Schedule();
            StatusMessage = auto
                ? $"已自動儲存選單：{_document.ConfigPath}"
                : $"已儲存選單：{_document.ConfigPath}";
            if (reload)
            {
                Refresh();
                StatusMessage = auto
                    ? $"已自動儲存選單：{_document.ConfigPath}"
                    : $"已儲存選單：{_document.ConfigPath}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = auto ? $"自動儲存失敗：{ex.Message}" : ex.Message;
        }

        await Task.CompletedTask;
    }

    private async Task SavePageCoreAsync(bool reload, bool auto)
    {
        if (SelectedPage is null)
        {
            if (!auto)
                StatusMessage = "請先選擇網站頁面";
            return;
        }

        try
        {
            var document = _services.FrontMatter.Parse(await _services.Content.ReadAsync(SelectedPage.FullPath));
            if (!string.IsNullOrWhiteSpace(PageTitle))
                document.Fields["title"] = PageTitle.Trim();
            document.Body = PageBody ?? string.Empty;
            await _services.Content.SaveAsync(SelectedPage.FullPath, _services.FrontMatter.Write(document));
            PageIsDirty = false;
            if (!IsDirty)
                _autoSave.Cancel();
            StatusMessage = auto
                ? $"已自動儲存頁面：{SelectedPage.RelativePath}"
                : $"已儲存頁面：{SelectedPage.RelativePath}";
            if (reload)
            {
                var selectedPath = SelectedPage.FullPath;
                Refresh();
                SelectedPage = SitePages.FirstOrDefault(page =>
                    page.FullPath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = auto ? $"自動儲存失敗：{ex.Message}" : ex.Message;
        }
    }

    [RelayCommand]
    private async Task CreatePageAsync()
    {
        if (!RequireProject(out _)) return;
        if (string.IsNullOrWhiteSpace(NewPageTitle))
        {
            StatusMessage = "請輸入頁面標題";
            return;
        }

        var folder = string.IsNullOrWhiteSpace(NewPageFolder) ? Slugify(NewPageTitle) : Slugify(NewPageFolder);
        try
        {
            var path = await _services.Content.CreatePageAsync(NewPageTitle.Trim(), folder);
            NewPageTitle = string.Empty;
            StatusMessage = $"已建立頁面：source/{folder}/index.md";
            Refresh();
            SelectedPage = SitePages.FirstOrDefault(page =>
                page.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task LoadPageAsync(ContentItem page)
    {
        _loading = true;
        try
        {
            var markdown = await _services.Content.ReadAsync(page.FullPath);
            var document = _services.FrontMatter.Parse(markdown);
            PageTitle = document.Fields.TryGetValue("title", out var title) ? title : page.DisplayTitle;
            PageBody = document.Body;
            PageIsDirty = false;
            StatusMessage = page.RelativePath;
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

    private void RebuildMenuNames()
    {
        var selected = SelectedMenuName;
        var names = _all.Select(item => string.IsNullOrWhiteSpace(item.MenuName) ? "menu" : item.MenuName.Trim())
            .Concat(["menu", "social"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.Equals("menu", StringComparison.OrdinalIgnoreCase) ? 0
                : name.Equals("social", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        MenuNames.Clear();
        foreach (var name in names)
            MenuNames.Add(name);

        if (!string.IsNullOrWhiteSpace(selected)
            && MenuNames.Any(name => name.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            SelectedMenuName = MenuNames.First(name => name.Equals(selected, StringComparison.OrdinalIgnoreCase));
        else if (MenuNames.Count > 0 && string.IsNullOrWhiteSpace(SelectedMenuName))
            SelectedMenuName = MenuNames[0];
    }

    private void RebuildVisibleEntries(bool preserveSelection)
    {
        var selected = preserveSelection ? SelectedEntry : null;
        var menu = string.IsNullOrWhiteSpace(SelectedMenuName) ? "menu" : SelectedMenuName;
        Entries.Clear();
        foreach (var item in _all
                     .Where(entry => entry.MenuName.Equals(menu, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.Weight)
                     .ThenBy(entry => entry.DisplayTitle, StringComparer.OrdinalIgnoreCase))
        {
            Entries.Add(item);
        }

        SelectedEntry = selected is not null && Entries.Contains(selected)
            ? selected
            : Entries.FirstOrDefault();
        MenuSummary = $"{_all.Count} 個選單項目 · {MenuNames.Count} 組選單 · {SitePages.Count} 個網站頁面";
    }

    private void MoveSelected(int delta)
    {
        if (SelectedEntry is null) return;
        var ordered = Entries.ToList();
        var index = ordered.IndexOf(SelectedEntry);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count) return;

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Weight = i + 1;

        MarkDirty();
        var keep = SelectedEntry;
        RebuildVisibleEntries(preserveSelection: false);
        SelectedEntry = keep;
    }

    private int NextWeight()
    {
        var menu = string.IsNullOrWhiteSpace(SelectedMenuName) ? "menu" : SelectedMenuName;
        var weights = _all
            .Where(item => item.MenuName.Equals(menu, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Weight)
            .ToList();
        return weights.Count == 0 ? 1 : weights.Max() + 1;
    }

    private void EnsureMenuName()
    {
        if (string.IsNullOrWhiteSpace(SelectedMenuName))
            SelectedMenuName = "menu";
        if (!MenuNames.Contains(SelectedMenuName))
            MenuNames.Add(SelectedMenuName);
    }

    private void UpdateEditorMode()
    {
        ShowEmptyEditor = !IsEditingMenu && !IsEditingPage;
    }

    private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading) return;
        MarkDirty();
        if (e.PropertyName != nameof(MenuEntryItem.MenuName) || sender is not MenuEntryItem item)
            return;

        RebuildMenuNames();
        if (!item.MenuName.Equals(SelectedMenuName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.MenuName))
            SelectedMenuName = item.MenuName;
        else
            RebuildVisibleEntries(preserveSelection: true);
    }

    private static string GuessIdentifier(ContentItem page)
    {
        var relative = page.RelativePath.Replace('\\', '/').Trim('/');
        if (relative.StartsWith("source/", StringComparison.OrdinalIgnoreCase))
            relative = relative["source/".Length..];
        var folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(folder))
            return folder.Split('/')[^1];
        return Path.GetFileNameWithoutExtension(relative);
    }

    private static string GuessIcon(string identifier) => identifier.ToLowerInvariant() switch
    {
        "home" => "fa fa-home",
        "archives" or "archive" => "fa fa-archive",
        "search" => "fa fa-search",
        "about" => "fa fa-user",
        "categories" or "category" => "fa fa-th",
        "tags" or "tag" => "fa fa-tags",
        _ => "fa fa-link"
    };

    private static string NormalizeUrl(string url)
    {
        var value = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value) || value == "/") return "/";
        return "/" + value.Trim('/') + "/";
    }

    private static string Slugify(string title)
    {
        var value = title.Trim().ToLowerInvariant();
        value = Regex.Replace(value, @"\s+", "-");
        value = Regex.Replace(value, @"[^a-z0-9\u4e00-\u9fff\-_]", "");
        return string.IsNullOrWhiteSpace(value) ? $"page-{DateTime.Now:yyyyMMddHHmmss}" : value;
    }

    public void Dispose()
    {
        _autoSave.Dispose();
    }
}
