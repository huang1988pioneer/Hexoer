using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Models;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class ThemeViewModel : PageViewModelBase
{
    private readonly ServiceHost _services;

    public override string Title => "主題 Themes";
    public override string Icon => "🎨";

    public ObservableCollection<ThemeInfo> Themes { get; } = new();

    [ObservableProperty] public partial ThemeInfo? SelectedTheme { get; set; }
    [ObservableProperty] public partial string ActiveTheme { get; set; } = "-";
    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CustomGitUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string CustomThemeName { get; set; } = string.Empty;

    public ThemeViewModel(ServiceHost services)
    {
        _services = services;
        _services.ProcessRunner.OutputReceived += line =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LogText += line + Environment.NewLine;
            });
    }

    public override async void OnNavigatedTo() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Themes.Clear();
        foreach (var t in _services.Themes.GetThemesWithInstallState())
            Themes.Add(t);

        ActiveTheme = await _services.Hexo.GetActiveThemeNameAsync() ?? "-";
        StatusMessage = $"目前主題：{ActiveTheme}";
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        if (SelectedTheme is null)
        {
            StatusMessage = "請先選擇主題";
            return;
        }

        await InstallThemeAsync(SelectedTheme);
    }

    [RelayCommand]
    private async Task InstallAndActivateAsync()
    {
        if (SelectedTheme is null)
        {
            StatusMessage = "請先選擇主題";
            return;
        }

        await InstallThemeAsync(SelectedTheme);
        await ActivateAsync(SelectedTheme.Name);
    }

    [RelayCommand]
    private async Task ActivateSelectedAsync()
    {
        if (SelectedTheme is null)
        {
            StatusMessage = "請先選擇主題";
            return;
        }

        await ActivateAsync(SelectedTheme.Name);
    }

    [RelayCommand]
    private async Task InstallCustomAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomGitUrl) || string.IsNullOrWhiteSpace(CustomThemeName))
        {
            StatusMessage = "請填寫主題名稱與 Git URL";
            return;
        }

        var theme = new ThemeInfo
        {
            Name = CustomThemeName.Trim(),
            DisplayName = CustomThemeName.Trim(),
            Description = "自訂主題",
            GitUrl = CustomGitUrl.Trim(),
            ConfigFileName = "_config.yml"
        };

        await InstallThemeAsync(theme);
    }

    private async Task InstallThemeAsync(ThemeInfo theme)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = $"正在安裝 {theme.DisplayName}…";
            var result = await _services.Themes.InstallThemeAsync(theme);
            LogText += result.CombinedOutput + Environment.NewLine;
            if (!result.Success)
            {
                StatusMessage = $"安裝失敗：{theme.DisplayName}";
                return;
            }

            StatusMessage = $"已安裝 {theme.DisplayName}";
            await RefreshAsync();
            SelectedTheme = theme;
        }
        catch (Exception ex)
        {
            StatusMessage = "安裝失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ActivateAsync(string themeName)
    {
        try
        {
            IsBusy = true;
            await _services.Themes.ActivateThemeAsync(themeName);
            ActiveTheme = themeName;
            StatusMessage = $"已啟用主題：{themeName}（已寫入 _config.yml）";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "啟用失敗：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
