using System;
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
                    ? "○ 尚未選擇專案"
                    : "✗ 不是有效的 Hexo 專案（缺少 _config.yml）";
            ThemeStatus = status.ThemeName ?? "-";
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

    [RelayCommand]
    private async Task InstallHexoCliAsync()
    {
        await RunStepAsync("安裝 hexo-cli（全域）…", async () =>
        {
            var r = await _services.Hexo.InstallHexoCliGlobalAsync();
            AppendLog(r.CombinedOutput);
            if (!r.Success) throw new InvalidOperationException("安裝 hexo-cli 失敗。");
        });
        await CheckEnvironmentAsync();
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
