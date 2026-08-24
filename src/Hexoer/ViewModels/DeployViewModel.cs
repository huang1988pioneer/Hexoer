using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Models;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class DeployViewModel : PageViewModelBase, IDisposable
{
    private static readonly TimeSpan DeploymentCheckInterval = TimeSpan.FromMinutes(5);
    private readonly ServiceHost _services;
    private readonly Action<string> _outputHandler;
    private readonly SemaphoreSlim _deploymentCheckGate = new(1, 1);
    private CancellationTokenSource? _deploymentMonitorCts;
    private DeploymentVersionState? _lastDeploymentState;
    private string? _lastExpectedDeploymentId;
    private bool _loadingProviderSettings;
    private bool _skipProviderSettingsLoad;

    public override string Title => "多平台 Pages";
    public override string Icon => "☁";

    public IReadOnlyList<RemoteProviderOption> RemoteProviders { get; } = RemoteProviderOption.All;
    [ObservableProperty] public partial RemoteProviderOption SelectedRepositoryProvider { get; set; } =
        RemoteProviderOption.FromProvider(RemoteGitProvider.GitHub);

    [ObservableProperty] public partial string GitStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string GhStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string RemoteSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string PagesSummary { get; set; } = "尚未查詢";
    [ObservableProperty] public partial string PagesUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string RepoName { get; set; } = string.Empty;
    [ObservableProperty] public partial string RepositoryUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string RepositoryTargetSummary { get; set; } =
        "貼上 GitHub、GitLab、Codeberg 或 Bitbucket repository 網址後，Hexoer 會先顯示目標與 Pages 網址。";
    [ObservableProperty] public partial bool CanConnectRepository { get; set; }
    [ObservableProperty] public partial bool SyncRecommendedSiteUrl { get; set; } = true;
    [ObservableProperty] public partial bool IsPublicRepo { get; set; } = true;
    [ObservableProperty] public partial string CommitMessage { get; set; } = "Update site via Hexoer";
    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeploymentMonitorTitle { get; set; } = "等待第一次部署";
    [ObservableProperty] public partial string DeploymentMonitorSummary { get; set; } =
        "推送網站後，Hexoer 會辨識線上網站是否已更新。";
    [ObservableProperty] public partial string DeploymentMonitorSchedule { get; set; } = "每 5 分鐘自動檢查";
    [ObservableProperty] public partial bool IsCheckingDeployment { get; set; }
    [ObservableProperty] public partial string DeployerRepoUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeployerBranch { get; set; } = "gh-pages";

    public DeployViewModel(ServiceHost services)
    {
        _services = services;
        var provider = _services.Project.Settings.GetSelectedDeployProvider();
        _loadingProviderSettings = true;
        SelectedRepositoryProvider = RemoteProviderOption.FromProvider(provider);
        _loadingProviderSettings = false;
        LoadDeployProviderSettings(provider);
        _outputHandler = line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _services.ProcessRunner.OutputReceived += _outputHandler;
    }

    partial void OnRepositoryUrlChanged(string value)
    {
        var target = ParseRepositoryTarget(value);
        CanConnectRepository = target.IsValid;
        if (!target.IsValid)
        {
            RepositoryTargetSummary = string.IsNullOrWhiteSpace(value)
                ? "選擇平台後，可貼上 repository 網址或輸入 owner/repo；各平台會分別保存。"
                : target.ErrorMessage;
            SaveDeployProviderSettings(SelectedRepositoryProvider.Provider);
            return;
        }

        if (target.Provider != SelectedRepositoryProvider.Provider && target.Provider != RemoteGitProvider.Unknown)
            SelectRepositoryProvider(target.Provider, loadSettings: false);

        RepoName = target.Repository!;
        SaveDeployProviderSettings(SelectedRepositoryProvider.Provider);
        RepositoryTargetSummary =
            $"平台：{target.ProviderName}\n" +
            $"Repository：{target.Owner}/{target.Repository}\n" +
            $"網站類型：{(target.IsUserOrOrganizationSite ? "使用者／組織網站" : "專案網站")}\n" +
            $"建議 Pages 網址：{target.PagesUrl}\n" +
            $"建議 _config.yml：url = {SiteUrlFromTarget(target)}，root = {RootFromTarget(target)}";
    }

    partial void OnSelectedRepositoryProviderChanged(RemoteProviderOption value)
    {
        if (_loadingProviderSettings)
            return;

        var provider = value.Provider;
        _services.Project.Settings.SetSelectedDeployProvider(provider);
        if (_skipProviderSettingsLoad)
        {
            _skipProviderSettingsLoad = false;
            SaveDeployProviderSettings(provider);
            return;
        }

        LoadDeployProviderSettings(provider);
    }

    partial void OnDeployerRepoUrlChanged(string value) =>
        SaveDeployProviderSettings(SelectedRepositoryProvider.Provider);

    partial void OnDeployerBranchChanged(string value) =>
        SaveDeployProviderSettings(SelectedRepositoryProvider.Provider);
    public override async void OnNavigatedTo()
    {
        try
        {
            await RefreshAsync();
            EnsureDeploymentMonitorStarted();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var gitOk = await _services.GitHub.IsGitAvailableAsync();
            var ghOk = await _services.GitHub.IsGhAvailableAsync();
            GitStatus = gitOk ? "Git：已安裝" : "Git：未找到（請安裝 Git for Windows）";
            GhStatus = ghOk ? "GitHub CLI (gh)：已安裝" : "GitHub CLI：未找到（請安裝 gh）";

            if (!RequireSite(out var site))
            {
                RemoteSummary = "尚未選擇網站";
                return;
            }

            if (string.IsNullOrWhiteSpace(RepoName))
                RepoName = Path.GetFileName(site.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var info = await _services.GitHub.GetInfoAsync(site);
            if (string.IsNullOrWhiteSpace(RepositoryUrl) && !string.IsNullOrWhiteSpace(info.RemoteUrl))
                RepositoryUrl = info.RemoteUrl;
            RemoteSummary =
                $"使用者：{info.GhUser ?? "（未登入）"}\n" +
                $"驗證：{(info.GhAuthenticated ? "已登入" : "未登入")}\n" +
                $"分支：{info.Branch ?? "—"}\n" +
                $"Remote：{info.RemoteUrl ?? "（無 origin）"}\n" +
                $"Repo：{(info.Owner is null ? "—" : $"{info.Owner}/{info.Repo}")}";

            try
            {
                var (repo, branch) = await _services.Config.ReadDeploySettingsAsync();
                if (string.IsNullOrWhiteSpace(DeployerRepoUrl) && !string.IsNullOrWhiteSpace(repo))
                    DeployerRepoUrl = repo;
                if (!string.IsNullOrWhiteSpace(branch))
                    DeployerBranch = branch;
            }
            catch
            {
                // ignore missing deploy block
            }

            await RefreshPagesStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectExistingRepositoryAsync()
    {
        if (!RequireSite(out var site)) return;
        var target = ParseRepositoryTarget(RepositoryUrl);
        if (!target.IsValid)
        {
            StatusMessage = target.ErrorMessage;
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = target.Provider == RemoteGitProvider.GitHub ? "正在確認 GitHub repository 推送權限…" : "將使用 git push 驗證遠端推送權限…";
            var access = await _services.GitHub.CheckPushAccessAsync(target);
            AppendLog(access.Message);
            if (!access.HasAccess)
            {
                StatusMessage = access.Message;
                return;
            }

            if (SyncRecommendedSiteUrl)
            {
                await _services.GitHub.UpdateSiteUrlAsync(target);
                AppendLog($"已將 _config.yml 的 url 設為 {SiteUrlFromTarget(target)}，root 設為 {RootFromTarget(target)}");
            }

            StatusMessage = "正在本機執行 hexo generate 驗證網站…";
            AppendLog("hexo generate…");
            var build = await _services.Hexo.GenerateAsync();
            AppendLog(build.CombinedOutput);
            if (!build.Success)
            {
                StatusMessage = "建置失敗；尚未連結或推送 repository。";
                return;
            }

            var progress = new Progress<string>(message =>
            {
                AppendLog(message);
                StatusMessage = message;
            });
            var result = await _services.GitHub.ConnectExistingRepositoryAndPushAsync(
                site,
                target,
                string.IsNullOrWhiteSpace(CommitMessage) ? "Publish site via Hexoer" : CommitMessage.Trim(),
                progress);
            AppendLog(result.CombinedOutput);
            StatusMessage = result.Success
                ? (target.Provider == RemoteGitProvider.GitHub ? "已連結 repository、推送網站並啟用 GitHub Pages" : $"已推送到 {target.ProviderName}")
                : "連結或部署失敗；請查看操作日誌";
            await RefreshAsync();
            await CheckDeploymentVersionAsync(manual: false, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        IsBusy = true;
        try
        {
            AppendLog("啟動 gh auth login…");
            StatusMessage = "請在瀏覽器完成 GitHub 登入";
            var result = await _services.GitHub.OpenGhAuthLoginAsync();
            AppendLog(result.CombinedOutput);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateAndDeployAsync()
    {
        if (!RequireSite(out var site)) return;
        if (string.IsNullOrWhiteSpace(RepoName))
        {
            StatusMessage = "請輸入 repository 名稱";
            return;
        }

        var existingTarget = ParseRepositoryTarget(RepoName);
        if (existingTarget.IsValid)
        {
            RepositoryUrl = RepoName.Trim();
            AppendLog($"偵測到既有 repository 網址，改用安全連結流程：{existingTarget.Owner}/{existingTarget.Repository}");
            await ConnectExistingRepositoryAsync();
            return;
        }

        if (RepoName.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            || RepoName.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase)
            || RepoName.Contains("codeberg.org", StringComparison.OrdinalIgnoreCase)
            || RepoName.Contains("bitbucket.org", StringComparison.OrdinalIgnoreCase)
            || RepoName.Contains('/')
            || RepoName.Contains('\\'))
        {
            StatusMessage = $"Repository 網址格式無效：{existingTarget.ErrorMessage}";
            AppendLog(StatusMessage);
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "正在本機執行 hexo generate 驗證網站…";
            AppendLog("hexo generate…");
            var build = await _services.Hexo.GenerateAsync();
            AppendLog(build.CombinedOutput);
            if (!build.Success)
            {
                StatusMessage = "建置失敗；尚未建立或推送 repository。";
                return;
            }

            var progress = new Progress<string>(message =>
            {
                AppendLog(message);
                StatusMessage = message;
            });
            var result = await _services.GitHub.CreateRepoAndPushAsync(
                site, RepoName.Trim(), IsPublicRepo, progress);
            AppendLog(result.CombinedOutput);
            StatusMessage = result.Success
                ? "已推送並嘗試啟用 Pages"
                : "部署過程有錯誤，請查看日誌";
            await RefreshAsync();
            await CheckDeploymentVersionAsync(manual: false, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        if (!RequireSite(out var site)) return;

        var info = await _services.GitHub.GetInfoAsync(site);
        if (string.IsNullOrWhiteSpace(info.RemoteUrl))
        {
            var candidate = !string.IsNullOrWhiteSpace(RepositoryUrl) ? RepositoryUrl : RepoName;
            var target = ParseRepositoryTarget(candidate);
            if (target.IsValid)
            {
                RepositoryUrl = candidate;
                AppendLog($"尚未設定 origin；改用安全連結流程：{target.Owner}/{target.Repository}");
                await ConnectExistingRepositoryAsync();
                return;
            }

            StatusMessage = "尚未連結 Git repository。請先在上方貼上完整 Repository URL，再按「連結、推送」。";
            AppendLog(StatusMessage);
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "正在本機執行 hexo generate 驗證網站…";
            AppendLog("hexo generate…");
            var build = await _services.Hexo.GenerateAsync();
            AppendLog(build.CombinedOutput);
            if (!build.Success)
            {
                StatusMessage = "建置失敗；已停止提交與推送。請依日誌修正網站內容。";
                return;
            }

            var progress = new Progress<string>(message =>
            {
                AppendLog(message);
                StatusMessage = message;
            });
            var result = await _services.GitHub.PushAsync(site, CommitMessage, progress);
            AppendLog(result.CombinedOutput);
            StatusMessage = result.Success ? "推送完成" : "推送失敗";
            await RefreshPagesStatusAsync();
            if (result.Success)
                await CheckDeploymentVersionAsync(manual: false, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnablePagesAsync()
    {
        if (!RequireSite(out var site)) return;
        IsBusy = true;
        try
        {
            await _services.GitHub.EnsureGitHubActionsWorkflowAsync(site);
            var result = await _services.GitHub.EnablePagesFromActionsAsync(site);
            AppendLog(result.CombinedOutput);
            StatusMessage = result.Success ? "已請求啟用 Pages" : "啟用失敗";
            await RefreshPagesStatusAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshPagesStatusAsync()
    {
        if (!RequireSite(out var site)) return;

        var status = await _services.GitHub.GetPagesStatusAsync(site);
        PagesUrl = status.HtmlUrl ?? string.Empty;
        PagesSummary =
            $"啟用：{(status.Enabled ? "是" : "否")}\n" +
            $"狀態：{status.Status ?? "—"}\n" +
            $"建置類型：{status.BuildType ?? "—"}\n" +
            $"來源分支：{status.SourceBranch ?? "—"}\n" +
            $"網址：{status.HtmlUrl ?? "—"}\n" +
            $"CNAME：{status.Cname ?? "—"}\n" +
            $"{status.Message}";
        StatusMessage = status.Message ?? string.Empty;
    }

    [RelayCommand]
    private Task CheckDeploymentNowAsync() =>
        CheckDeploymentVersionAsync(manual: true, CancellationToken.None);

    [RelayCommand]
    private async Task AddWorkflowOnlyAsync()
    {
        if (!RequireSite(out var site)) return;
        await _services.GitHub.EnsureGitHubActionsWorkflowAsync(site);
        StatusMessage = "已寫入 .github/workflows/hexo.yml（若尚未存在）";
        AppendLog(StatusMessage);
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

    [RelayCommand]
    private async Task SaveDeployConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(DeployerRepoUrl))
        {
            StatusMessage = "請填寫 Git repo URL";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "寫入 deploy 設定並安裝 hexo-deployer-git…";
            await _services.GitHub.ConfigureDeployAsync(
                ResolveDeployRepoUrl(),
                string.IsNullOrWhiteSpace(DeployerBranch) ? "gh-pages" : DeployerBranch.Trim());
            SaveDeployProviderSettings(SelectedRepositoryProvider.Provider);
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
    private async Task DeployWithGitAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = "正在 generate + hexo deploy…";
            AppendLog("—— Deploy 開始");
            var result = await _services.GitHub.DeployAsync();
            AppendLog(result.CombinedOutput);
            StatusMessage = result.Success ? "部署完成" : "部署失敗，請查看日誌";
            if (result.Success)
                await RefreshPagesStatusAsync();
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

    private GitHubRepositoryTarget ParseRepositoryTarget(string? input) =>
        GitHubService.ParseRepositoryTarget(input, SelectedRepositoryProvider.Provider);

    private void SelectRepositoryProvider(RemoteGitProvider provider, bool loadSettings)
    {
        if (provider is RemoteGitProvider.Unknown || provider == SelectedRepositoryProvider.Provider)
            return;

        _skipProviderSettingsLoad = !loadSettings;
        SelectedRepositoryProvider = RemoteProviderOption.FromProvider(provider);
    }

    private void LoadDeployProviderSettings(RemoteGitProvider provider)
    {
        _loadingProviderSettings = true;
        try
        {
            var settings = _services.Project.Settings.GetRemoteProviderSettings(provider);
            RepositoryUrl = settings.RepositoryUrl ?? string.Empty;
            DeployerRepoUrl = settings.DeployerRepoUrl ?? string.Empty;
            DeployerBranch = string.IsNullOrWhiteSpace(settings.DeployerBranch) ? "gh-pages" : settings.DeployerBranch;
        }
        finally
        {
            _loadingProviderSettings = false;
        }

        OnRepositoryUrlChanged(RepositoryUrl);
    }

    private void SaveDeployProviderSettings(RemoteGitProvider provider)
    {
        if (_loadingProviderSettings)
            return;

        var settings = _services.Project.Settings.GetRemoteProviderSettings(provider);
        settings.RepositoryUrl = RepositoryUrl.Trim();
        settings.DeployerRepoUrl = DeployerRepoUrl.Trim();
        settings.DeployerBranch = string.IsNullOrWhiteSpace(DeployerBranch) ? "gh-pages" : DeployerBranch.Trim();
        _services.Project.Settings.Save();
    }

    private string ResolveDeployRepoUrl()
    {
        var target = ParseRepositoryTarget(DeployerRepoUrl);
        return target.IsValid && !string.IsNullOrWhiteSpace(target.CanonicalUrl)
            ? target.CanonicalUrl
            : DeployerRepoUrl.Trim();
    }
    public void Dispose()
    {
        _deploymentMonitorCts?.Cancel();
        _deploymentMonitorCts?.Dispose();
        _deploymentMonitorCts = null;
        _services.ProcessRunner.OutputReceived -= _outputHandler;
        _deploymentCheckGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void EnsureDeploymentMonitorStarted()
    {
        if (_deploymentMonitorCts is not null) return;
        _deploymentMonitorCts = new CancellationTokenSource();
        _ = MonitorDeploymentLoopAsync(_deploymentMonitorCts.Token);
    }

    private async Task MonitorDeploymentLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => CheckDeploymentVersionAsync(manual: false, cancellationToken));
                await Task.Delay(DeploymentCheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The application is closing.
        }
    }

    private async Task CheckDeploymentVersionAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!await _deploymentCheckGate.WaitAsync(0, cancellationToken)) return;

        IsCheckingDeployment = true;
        DeploymentMonitorTitle = "正在檢查線上版本…";
        DeploymentMonitorSchedule = "每 5 分鐘自動檢查 · 正在連線";
        try
        {
            if (!RequireSite(out var site))
            {
                DeploymentMonitorTitle = "尚未選擇網站";
                DeploymentMonitorSummary = "請先在「環境設定」開啟、建立，或從遠端 Git 複製 Hexo 網站。";
                DeploymentMonitorSchedule = "選擇網站後開始每 5 分鐘檢查";
                return;
            }

            if (string.IsNullOrWhiteSpace(PagesUrl))
            {
                var pages = await _services.GitHub.GetPagesStatusAsync(site, cancellationToken: cancellationToken);
                PagesUrl = pages.HtmlUrl ?? string.Empty;
            }

            var result = await _services.DeploymentMonitor.CheckAsync(site, PagesUrl, cancellationToken);
            var stateChanged = result.State != _lastDeploymentState
                               || !string.Equals(result.ExpectedDeploymentId, _lastExpectedDeploymentId,
                                   StringComparison.Ordinal);

            DeploymentMonitorTitle = result.State switch
            {
                DeploymentVersionState.Latest => "線上網站已是最新版本",
                DeploymentVersionState.Previous => "線上網站仍是上一版本",
                DeploymentVersionState.Unavailable => "暫時無法檢查",
                _ => "等待下一次部署"
            };
            DeploymentMonitorSummary = result.Message;
            DeploymentMonitorSchedule =
                $"每 5 分鐘自動檢查 · 上次：{result.CheckedAt.LocalDateTime:yyyy/MM/dd HH:mm:ss}";

            if (manual || stateChanged)
                AppendLog($"線上版本監控：{result.Message}");

            if (result.State == DeploymentVersionState.Latest && stateChanged)
            {
                StatusMessage = "網站已更新：線上內容是最新版本。";
                _services.SetAppStatus("網站已更新為最新版本");
            }
            else if (result.State == DeploymentVersionState.Previous && stateChanged)
            {
                StatusMessage = "線上網站仍是上一版本，將在 5 分鐘後再次檢查。";
                _services.SetAppStatus("線上網站仍是上一版本 · 自動監控中");
            }
            else if (manual)
            {
                StatusMessage = result.Message;
            }

            _lastDeploymentState = result.State;
            _lastExpectedDeploymentId = result.ExpectedDeploymentId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception ex)
        {
            DeploymentMonitorTitle = "暫時無法檢查";
            DeploymentMonitorSummary = $"檢查線上版本時發生錯誤：{ex.Message}";
            DeploymentMonitorSchedule = "每 5 分鐘自動檢查 · 稍後重試";
            if (manual) AppendLog(DeploymentMonitorSummary);
        }
        finally
        {
            IsCheckingDeployment = false;
            _deploymentCheckGate.Release();
        }
    }

    private bool RequireSite(out string sitePath)
    {
        sitePath = _services.Project.ProjectPath ?? string.Empty;
        if (!_services.Project.IsHexoProject)
        {
            StatusMessage = "請先在「環境設定」開啟、建立，或從遠端 Git 複製 Hexo 網站。";
            return false;
        }

        return true;
    }

    private static string SiteUrlFromTarget(GitHubRepositoryTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.PagesUrl) || !Uri.TryCreate(target.PagesUrl, UriKind.Absolute, out var uri))
            return string.Empty;
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static string RootFromTarget(GitHubRepositoryTarget target)
    {
        if (target.IsUserOrOrganizationSite || string.IsNullOrWhiteSpace(target.PagesUrl)
            || !Uri.TryCreate(target.PagesUrl, UriKind.Absolute, out var uri))
        {
            return "/";
        }

        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(path) ? "/" : $"/{path}/";
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
        LogText = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
        if (LogText.Length > 80_000)
            LogText = LogText[^60_000..];
    }
}
