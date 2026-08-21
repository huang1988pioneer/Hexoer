using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class ConfigViewModel : PageViewModelBase
{
    private readonly ServiceHost _services;

    public override string Title => "站點設定";
    public override string Icon => "✎";

    [ObservableProperty] public partial string ConfigText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TitleField { get; set; } = string.Empty;
    [ObservableProperty] public partial string SubtitleField { get; set; } = string.Empty;
    [ObservableProperty] public partial string DescriptionField { get; set; } = string.Empty;
    [ObservableProperty] public partial string AuthorField { get; set; } = string.Empty;
    [ObservableProperty] public partial string LanguageField { get; set; } = "zh-TW";
    [ObservableProperty] public partial string UrlField { get; set; } = string.Empty;
    [ObservableProperty] public partial string PermalinkField { get; set; } = ":year/:month/:day/:title/";
    [ObservableProperty] public partial bool IsAdvancedMode { get; set; } = true;
    [ObservableProperty] public partial bool HasUnsavedChanges { get; set; }

    public ConfigViewModel(ServiceHost services)
    {
        _services = services;
    }

    public override async void OnNavigatedTo() => await LoadAsync();

    partial void OnConfigTextChanged(string value) => HasUnsavedChanges = true;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!_services.Config.ConfigExists)
        {
            ConfigText = string.Empty;
            StatusMessage = "尚未選擇有效專案，或找不到 _config.yml";
            return;
        }

        try
        {
            IsBusy = true;
            ConfigText = await _services.Config.ReadSiteConfigAsync();
            var keys = await _services.Config.ReadSimpleKeysAsync(
                "title", "subtitle", "description", "author", "language", "url", "permalink");
            static string Get(System.Collections.Generic.Dictionary<string, string> map, string key, string fallback = "")
                => map.TryGetValue(key, out var v) ? v : fallback;

            TitleField = Get(keys, "title");
            SubtitleField = Get(keys, "subtitle");
            DescriptionField = Get(keys, "description");
            AuthorField = Get(keys, "author");
            LanguageField = Get(keys, "language", "zh-TW");
            UrlField = Get(keys, "url");
            PermalinkField = Get(keys, "permalink", ":year/:month/:day/:title/");
            HasUnsavedChanges = false;
            StatusMessage = "已載入 _config.yml";
        }
        catch (Exception ex)
        {
            StatusMessage = "載入失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_services.Config.ConfigExists)
        {
            StatusMessage = "沒有可儲存的設定檔";
            return;
        }

        try
        {
            IsBusy = true;
            if (!IsAdvancedMode)
            {
                await _services.Config.UpsertSimpleKeyAsync("title", TitleField);
                await _services.Config.UpsertSimpleKeyAsync("subtitle", SubtitleField);
                await _services.Config.UpsertSimpleKeyAsync("description", DescriptionField);
                await _services.Config.UpsertSimpleKeyAsync("author", AuthorField);
                await _services.Config.UpsertSimpleKeyAsync("language", LanguageField);
                await _services.Config.UpsertSimpleKeyAsync("url", UrlField);
                await _services.Config.UpsertSimpleKeyAsync("permalink", PermalinkField);
                ConfigText = await _services.Config.ReadSiteConfigAsync();
            }
            else
            {
                await _services.Config.SaveSiteConfigAsync(ConfigText);
            }

            HasUnsavedChanges = false;
            StatusMessage = "已儲存 _config.yml";
        }
        catch (Exception ex)
        {
            StatusMessage = "儲存失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyQuickFieldsAsync()
    {
        IsAdvancedMode = false;
        await SaveAsync();
        IsAdvancedMode = true;
        await LoadAsync();
    }
}
