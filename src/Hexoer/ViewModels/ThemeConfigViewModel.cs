using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class ThemeConfigViewModel : PageViewModelBase
{
    private readonly ServiceHost _services;

    public override string Title => "主題設定";
    public override string Icon => "🎛";

    public ObservableCollection<string> InstalledThemes { get; } = new();

    [ObservableProperty] public partial string? SelectedThemeName { get; set; }
    [ObservableProperty] public partial string ConfigText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ConfigPath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasUnsavedChanges { get; set; }

    public ThemeConfigViewModel(ServiceHost services)
    {
        _services = services;
    }

    public override async void OnNavigatedTo()
    {
        await RefreshThemesAsync();
        var active = await _services.Hexo.GetActiveThemeNameAsync();
        if (!string.IsNullOrWhiteSpace(active))
            SelectedThemeName = active;
        else if (InstalledThemes.Count > 0)
            SelectedThemeName = InstalledThemes[0];
    }

    partial void OnSelectedThemeNameChanged(string? value) => _ = LoadConfigAsync();
    partial void OnConfigTextChanged(string value) => HasUnsavedChanges = true;

    [RelayCommand]
    private async Task RefreshThemesAsync()
    {
        InstalledThemes.Clear();
        foreach (var name in _services.Themes.GetInstalledThemeNames())
            InstalledThemes.Add(name);
        StatusMessage = $"已安裝 {InstalledThemes.Count} 個主題";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedThemeName))
        {
            ConfigText = string.Empty;
            ConfigPath = string.Empty;
            return;
        }

        try
        {
            IsBusy = true;
            ConfigPath = _services.Themes.GetThemeConfigPath(SelectedThemeName) ?? "(找不到設定檔)";
            ConfigText = await _services.Themes.ReadThemeConfigAsync(SelectedThemeName) ?? string.Empty;
            HasUnsavedChanges = false;
            StatusMessage = string.IsNullOrEmpty(ConfigText)
                ? $"主題 {SelectedThemeName} 沒有設定檔"
                : $"已載入 {ConfigPath}";
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
        if (string.IsNullOrWhiteSpace(SelectedThemeName))
        {
            StatusMessage = "請選擇主題";
            return;
        }

        try
        {
            IsBusy = true;
            await _services.Themes.SaveThemeConfigAsync(SelectedThemeName, ConfigText);
            ConfigPath = _services.Themes.GetThemeConfigPath(SelectedThemeName) ?? ConfigPath;
            HasUnsavedChanges = false;
            StatusMessage = "主題設定已儲存";
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
}
