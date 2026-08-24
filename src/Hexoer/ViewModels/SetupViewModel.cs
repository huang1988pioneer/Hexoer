using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hexoer.Helpers;
using Hexoer.Services;

namespace Hexoer.ViewModels;

public partial class SetupViewModel : PageViewModelBase
{
    private readonly ServiceHost _services;

    public override string Title => "環境設定";
    public override string Icon => "⚙";

    [ObservableProperty] public partial string ProjectPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string SiteName { get; set; } = "my-blog";
    [ObservableProperty] public partial string NodeStatus { get; set; } = "未檢查";
    [ObservableProperty] public partial string NpmStatus { get; set; } = "未檢查";
    [ObservableProperty] public partial string GitStatus { get; set; } = "未檢查";
    [ObservableProperty] public partial string HexoStatus { get; set; } = "未檢查";
    [ObservableProperty] public partial string ProjectStatus { get; set; } = "未選擇";
    [ObservableProperty] public partial string ThemeStatus { get; set; } = "-";
    [ObservableProperty] public partial string GitIdentityStatus { get; set; } = "未檢查";
    [ObservableProperty] public partial string GitHubStatus { get; set; } = "未檢查";
    [ObservableProperty] public partial string RepositoryUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string CloneRepositoryUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string CloneTargetSummary { get; set; } =
        "貼上 GitHub、GitLab、Codeberg、Bitbucket repository 或 Pages 網址後，Hexoer 會顯示將複製的目標。";
    [ObservableProperty] public partial bool CanCloneRepository { get; set; }
    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool AllReady { get; set; }
    [ObservableProperty] public partial bool IsServerRunning { get; set; }
    [ObservableProperty] public partial string ServerUrl { get; set; } = "http://localhost:4000/";
    [ObservableProperty] public partial int ServerPort { get; set; } = 4000;

    public SetupViewModel(ServiceHost services)
    {
        _services = services;
        ProjectPath = services.Project.ProjectPath ?? string.Empty;
        _services.ProcessRunner.OutputReceived += line =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(line));
        _services.Server.OutputReceived += line =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(line));
        _services.Server.StateChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshServerState);
        RefreshServerState();
    }

    public override async void OnNavigatedTo()
    {
        ProjectPath = _services.Project.ProjectPath ?? string.Empty;
        RefreshServerState();
        await CheckEnvironmentAsync();
    }

    [RelayCommand]
    private async Task BrowseProjectAsync()
    {
        var folder = await DialogHelper.PickFolderAsync("選擇 Hexo 專案資料夾");
        if (string.IsNullOrWhiteSpace(folder)) return;
        ProjectPath = folder;
        _services.Project.ProjectPath = folder;
        await CheckEnvironmentAsync();
    }

    [RelayCommand]
    private async Task CheckEnvironmentAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = "正在檢查環境…";
            if (!string.IsNullOrWhiteSpace(ProjectPath))
                _services.Project.ProjectPath = ProjectPath;

            var status = await _services.Hexo.CheckEnvironmentAsync();
            NodeStatus = status.NodeInstalled ? $"✓ {status.NodeVersion}" : "✗ 未安裝 Node.js";
            NpmStatus = status.NpmInstalled ? $"✓ {status.NpmVersion}" : "✗ 未安裝 npm";
            GitStatus = status.GitInstalled ? $"✓ {status.GitVersion}" : "✗ 未安裝 Git";
            HexoStatus = status.HexoCliInstalled
                ? $"✓ {status.HexoVersion ?? "可用"}"
                : "○ 尚未全域安裝（可使用 npx）";
            ProjectStatus = status.ProjectValid
                ? $"✓ 有效專案：{status.ProjectPath}"
                : string.IsNullOrWhiteSpace(status.ProjectPath)
                    ? "○ 尚未選擇專案（可一鍵建立，或從遠端 Git 複製）"
                    : "✗ 不是有效的 Hexo 專案（缺少 _config.yml）";
            ThemeStatus = status.ThemeName ?? "-";
            var identity = await _services.Git.GetIdentityAsync();
            GitIdentityStatus = identity.UserName is not null && identity.Email is not null
                ? $"✓ {identity.UserName} <{identity.Email}>" : "○ 尚未設定 git user.name / user.email";
            GitHubStatus = identity.GitHubAuthenticated ? "✓ GitHub CLI 已登入" : "○ 尚未透過 GitHub CLI 登入";
            AllReady = status.NodeInstalled && status.NpmInstalled && status.GitInstalled && status.ProjectValid;
            StatusMessage = AllReady ? "環境就緒" : "請完成環境或專案設定";
        }
        catch (Exception ex)
        {
            StatusMessage = "檢查失敗：" + ex.Message;
            AppendLog(ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnCloneRepositoryUrlChanged(string value)
    {
        var target = GitHubService.ParseRepositoryTarget(value);
        CanCloneRepository = target.IsValid;
        if (!target.IsValid)
        {
            CloneTargetSummary = string.IsNullOrWhiteSpace(value)
                ? "貼上 GitHub、GitLab、Codeberg、Bitbucket repository 或 Pages 網址後，Hexoer 會顯示將複製的目標。"
                : target.ErrorMessage;
            return;
        }

        CloneTargetSummary =
            $"平台：{target.ProviderName}\n" +
            $"Repository：{target.Owner}/{target.Repository}\n" +
            $"網站類型：{(target.IsUserOrOrganizationSite ? "使用者／組織網站" : "專案網站")}\n" +
            $"建議 Pages 網址：{target.PagesUrl}\n" +
            "本機沒有網站時，可把 GitHub 上的 Hexo 原始碼複製下來繼續編輯。";
    }

    [RelayCommand]
    private async Task DetectGitHubPagesAsync()
    {
        await RunStepAsync("偵測 GitHub Pages 網站…", async () =>
        {
            var ghOk = await _services.GitHub.IsGhAvailableAsync();
            if (!ghOk)
                throw new InvalidOperationException("找不到 GitHub CLI。請安裝 gh 並執行 gh auth login。");

            var repos = await _services.GitHub.ListLikelyPagesRepositoriesAsync();
            if (repos.Count == 0)
            {
                var identity = await _services.Git.GetIdentityAsync();
                throw new InvalidOperationException(
                    identity.GitHubAuthenticated
                        ? "找不到 *.github.io repository。請貼上 Git repository 或 https://USERNAME.github.io/ 網址。"
                        : "尚未透過 GitHub CLI 登入。請先 gh auth login，或直接貼上 repository 網址。");
            }

            foreach (var repo in repos)
                AppendLog($"找到：{repo.Owner}/{repo.Repository} → {repo.PagesUrl}");

            var selected = repos[0];
            CloneRepositoryUrl = selected.CanonicalUrl ?? $"https://github.com/{selected.Owner}/{selected.Repository}";
            if (string.IsNullOrWhiteSpace(RepositoryUrl))
                RepositoryUrl = CloneRepositoryUrl;
            StatusMessage = $"已填入 {selected.Owner}/{selected.Repository}";
        });
    }

    [RelayCommand]
    private async Task InspectRemoteSiteAsync()
    {
        var target = GitHubService.ParseRepositoryTarget(CloneRepositoryUrl);
        if (!target.IsValid)
        {
            CloneTargetSummary = target.ErrorMessage;
            StatusMessage = target.ErrorMessage;
            return;
        }

        await RunStepAsync("檢查遠端 遠端網站…", async () =>
        {
            var info = await _services.GitHub.InspectRemoteSiteAsync(target);
            CloneTargetSummary = info.Message;
            AppendLog(info.Message);
            if (!info.Success)
                throw new InvalidOperationException(info.Message.Split('\n')[0]);
            StatusMessage = info.LooksLikeHexoSource
                ? "遠端有 Hexo 原始碼，可以複製到本機。"
                : info.LooksLikeGeneratedSite
                    ? "遠端是靜態網站，沒有 Hexo 原始碼。"
                    : "遠端檢查完成。";
        });
    }

    [RelayCommand]
    private async Task CloneFromGitHubAsync()
    {
        var target = GitHubService.ParseRepositoryTarget(CloneRepositoryUrl);
        if (!target.IsValid)
        {
            StatusMessage = target.ErrorMessage;
            CloneTargetSummary = target.ErrorMessage;
            return;
        }

        string? cloneMessage = null;
        await RunStepAsync("從遠端複製網站到本機…", async () =>
        {
            var preferred = await ResolveClonePreferredPathAsync();
            if (string.IsNullOrWhiteSpace(preferred))
                throw new InvalidOperationException("已取消。");

            var progress = new Progress<string>(message =>
            {
                AppendLog(message);
                StatusMessage = message;
            });
            var result = await _services.GitHub.CloneSiteToLocalAsync(target, preferred, progress);
            AppendLog(result.Message);
            if (!result.Success)
                throw new InvalidOperationException(result.Message.Split('\n')[0]);

            if (!string.IsNullOrWhiteSpace(result.LocalPath) && result.LooksLikeHexoSource)
            {
                ProjectPath = result.LocalPath;
                RepositoryUrl = target.CanonicalUrl ?? CloneRepositoryUrl;
            }

            CloneTargetSummary = result.Message;
            cloneMessage = result.LooksLikeHexoSource
                ? result.Message
                : "已複製檔案，但遠端沒有 Hexo 原始碼。";
            StatusMessage = cloneMessage;
        });
        await CheckEnvironmentAsync();
        if (!string.IsNullOrWhiteSpace(cloneMessage))
            StatusMessage = cloneMessage;
    }

    [RelayCommand]
    private async Task OneClickSetupAsync()
    {
        await RunStepAsync("一鍵建立 Hexo 環境…", async () =>
        {
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                var folder = await DialogHelper.PickFolderAsync("選擇要建立 Hexo 的資料夾");
                if (string.IsNullOrWhiteSpace(folder))
                    throw new InvalidOperationException("已取消。");
                ProjectPath = folder;
            }

            AppendLog($"目標資料夾：{ProjectPath}");
            var init = await _services.Hexo.InitProjectAsync(ProjectPath, SiteName);
            AppendLog(init.CombinedOutput);
            if (!init.Success)
                throw new InvalidOperationException("hexo init 失敗。");

            _services.Project.ProjectPath = ProjectPath;
            var install = await _services.Hexo.InstallDependenciesAsync();
            AppendLog(install.CombinedOutput);
            if (!install.Success)
                throw new InvalidOperationException("npm install 失敗。");

            var deployer = await _services.Hexo.InstallDeployerGitAsync();
            AppendLog(deployer.CombinedOutput);

            StatusMessage = "一鍵建立完成！";
        });
        await CheckEnvironmentAsync();
    }

    [RelayCommand]
    private async Task InstallDependenciesAsync()
    {
        await RunStepAsync("安裝專案依賴 npm install…", async () =>
        {
            if (!string.IsNullOrWhiteSpace(ProjectPath))
                _services.Project.ProjectPath = ProjectPath;
            var r = await _services.Hexo.InstallDependenciesAsync();
            AppendLog(r.CombinedOutput);
            if (!r.Success) throw new InvalidOperationException("npm install 失敗。");
        });
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        await RunStepAsync("執行 hexo generate…", async () =>
        {
            var r = await _services.Hexo.GenerateAsync();
            AppendLog(r.CombinedOutput);
            if (!r.Success) throw new InvalidOperationException("generate 失敗。");
        });
    }

    [RelayCommand]
    private async Task InitializeGitHubAsync()
    {
        await RunStepAsync("建立 Git 遠端並推送 main…", async () =>
        {
            await _services.Git.WritePagesWorkflowAsync();
            var result = await _services.Git.InitializeAndPushAsync(RepositoryUrl.Trim());
            AppendLog(result.CombinedOutput);
            if (!result.Success) throw new InvalidOperationException("Git 初始化或推送失敗。請確認 gh auth login 與 repository 權限。");
            StatusMessage = "已推送 main。GitHub 會自動加入 Actions workflow；其他平台請在平台端設定 Pages/CI 或 deploy 分支。";
        });
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        await RunStepAsync("執行 hexo clean…", async () =>
        {
            var r = await _services.Hexo.CleanAsync();
            AppendLog(r.CombinedOutput);
        });
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = "啟動 hexo server…";
            AppendLog("—— 啟動 hexo server");
            if (!string.IsNullOrWhiteSpace(ProjectPath))
                _services.Project.ProjectPath = ProjectPath;
            await _services.Server.StartAsync(ServerPort, openBrowser: true);
            RefreshServerState();
            StatusMessage = $"本機預覽：{_services.Server.PreviewUrl}";
            AppendLog($"Hexo server: {_services.Server.PreviewUrl}");
        }
        catch (Exception ex)
        {
            StatusMessage = "啟動 server 失敗：" + ex.Message;
            AppendLog(ex.Message);
            RefreshServerState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StopServer()
    {
        _services.Server.Stop();
        RefreshServerState();
        StatusMessage = "已停止 hexo server";
        AppendLog("[hexo server] 已手動停止");
    }

    [RelayCommand]
    private void OpenServerInBrowser()
    {
        _services.Server.OpenPreviewInBrowser();
        StatusMessage = "已開啟 " + _services.Server.PreviewUrl;
    }

    private async Task<string?> ResolveClonePreferredPathAsync()
    {
        if (_services.Project.IsHexoProject && !string.IsNullOrWhiteSpace(ProjectPath))
        {
            var parent = Path.GetDirectoryName(
                ProjectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                AppendLog($"本機已有 Hexo 專案，改複製到同一層資料夾：{parent}");
                return parent;
            }
        }

        if (!string.IsNullOrWhiteSpace(ProjectPath))
            return ProjectPath;

        var folder = await DialogHelper.PickFolderAsync("選擇要複製 遠端網站的資料夾");
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        ProjectPath = folder;
        return folder;
    }

    private void RefreshServerState()
    {
        IsServerRunning = _services.Server.IsRunning;
        ServerUrl = _services.Server.PreviewUrl;
        ServerPort = _services.Server.Port;
    }

    private async Task RunStepAsync(string message, Func<Task> action)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = message;
            AppendLog("—— " + message);
            await action();
            if (StatusMessage == message)
                StatusMessage = "完成";
        }
        catch (Exception ex)
        {
            StatusMessage = "失敗：" + ex.Message;
            AppendLog(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        LogText += line + Environment.NewLine;
        if (LogText.Length > 80_000)
            LogText = LogText[^60_000..];
    }
}



