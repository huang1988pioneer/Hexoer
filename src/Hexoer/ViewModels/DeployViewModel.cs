using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class DeployViewModel : PageViewModelBase
{
    private readonly ServiceHost _services;

    public override string Title => "GitHub Pages";
    public override string Icon => "☁";

    [ObservableProperty] public partial string RepoUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string Branch { get; set; } = "gh-pages";
    [ObservableProperty] public partial string Owner { get; set; } = string.Empty;
    [ObservableProperty] public partial string RepoName { get; set; } = string.Empty;
    [ObservableProperty] public partial string PagesStatusText { get; set; } = "尚未查詢";
    [ObservableProperty] public partial string PagesUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;

    public DeployViewModel(ServiceHost services)
    {
        _services = services;
        _services.ProcessRunner.OutputReceived += line =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LogText += line + Environment.NewLine;
            });
    }

    public override async void OnNavigatedTo()
    {
        try
        {
            var (repo, branch) = await _services.Config.ReadDeploySettingsAsync();
            if (!string.IsNullOrWhiteSpace(repo))
                RepoUrl = repo;
            if (!string.IsNullOrWhiteSpace(branch))
                Branch = branch;

            var (owner, name) = await _services.GitHub.TryParseRepoFromConfigAsync();
            if (!string.IsNullOrWhiteSpace(owner)) Owner = owner;
            if (!string.IsNullOrWhiteSpace(name)) RepoName = name;
        }
        catch
        {
            // ignore
        }
    }

    [RelayCommand]
    private async Task SaveDeployConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(RepoUrl))
        {
            StatusMessage = "請填寫 GitHub repo URL";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "寫入 deploy 設定並安裝 hexo-deployer-git…";
            await _services.GitHub.ConfigureDeployAsync(RepoUrl.Trim(), string.IsNullOrWhiteSpace(Branch) ? "gh-pages" : Branch.Trim());
            var (owner, name) = GitHubService.ParseOwnerRepo(RepoUrl);
            if (owner is not null) Owner = owner;
            if (name is not null) RepoName = name;
            StatusMessage = "Deploy 設定完成";
        }
        catch (Exception ex)
        {
            StatusMessage = "設定失敗：" + ex.Message;
            AppendLog(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeployAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = "正在 generate + deploy…";
            AppendLog("—— Deploy 開始");
            var result = await _services.GitHub.DeployAsync();
            AppendLog(result.CombinedOutput);
            StatusMessage = result.Success ? "部署完成" : "部署失敗，請查看日誌";
            if (result.Success)
                await CheckPagesStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "部署失敗：" + ex.Message;
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckPagesStatusAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "查詢 GitHub Pages 狀態…";

            var status = await _services.GitHub.GetPagesStatusAsync(
                string.IsNullOrWhiteSpace(Owner) ? null : Owner.Trim(),
                string.IsNullOrWhiteSpace(RepoName) ? null : RepoName.Trim());

            PagesStatusText = status.Success
                ? $"狀態：{status.Status}\n來源：{status.SourceBranch} {status.SourcePath}\n更新：{status.UpdatedAt}\n{status.Message}"
                : status.Message ?? "查詢失敗";
            PagesUrl = status.HtmlUrl ?? string.Empty;
            StatusMessage = status.Success ? $"Pages: {status.Status}" : "無法取得 Pages 狀態";
        }
        catch (Exception ex)
        {
            StatusMessage = "查詢失敗：" + ex.Message;
            PagesStatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenPagesUrl()
    {
        if (string.IsNullOrWhiteSpace(PagesUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(PagesUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "無法開啟瀏覽器：" + ex.Message;
        }
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        LogText += line + Environment.NewLine;
    }
}
