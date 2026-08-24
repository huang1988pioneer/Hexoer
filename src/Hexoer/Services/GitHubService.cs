using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

public sealed partial class GitHubService
{
    private readonly ProjectContext _context;
    private readonly ConfigService _config;
    private readonly HexoService _hexo;
    private readonly ProcessRunner _runner;
    private readonly DeploymentMonitorService _deploymentMonitor;

    public GitHubService(
        ProjectContext context,
        ConfigService config,
        HexoService hexo,
        ProcessRunner runner,
        DeploymentMonitorService? deploymentMonitor = null)
    {
        _context = context;
        _config = config;
        _hexo = hexo;
        _runner = runner;
        _deploymentMonitor = deploymentMonitor ?? new DeploymentMonitorService();
    }

    public static GitHubRepositoryTarget ParseRepositoryTarget(string? input)
    {
        var value = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return InvalidTarget("請貼上 GitHub、GitLab、Codeberg 或 Bitbucket repository / Pages 網址。");

        var ssh = GenericSshRemoteRegex().Match(value);
        if (ssh.Success)
        {
            var sshProvider = ProviderFromHost(ssh.Groups["host"].Value);
            if (sshProvider == RemoteGitProvider.Unknown)
                return InvalidTarget("目前支援 GitHub、GitLab、Codeberg 與 Bitbucket repository URL。");
            return FromProviderPath(sshProvider, ssh.Groups["path"].Value);
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (LooksLikeKnownHostPath(value) || LooksLikeKnownPagesHost(value))
            {
                value = $"https://{value}";
            }
            else
            {
                value = $"https://github.com/{value.TrimStart('/')}";
            }
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return InvalidTarget(
                "僅支援 HTTPS repository URL、git@host:owner/repository.git，或 GitHub/GitLab/Codeberg/Bitbucket Pages 網址。");
        }

        var provider = ProviderFromHost(uri.Host);
        if (provider != RemoteGitProvider.Unknown)
        {
            var repoPath = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(repoPath))
                return InvalidTarget("網址必須指向 repository，例如 https://github.com/owner/repository。");

            return FromProviderPath(provider, repoPath);
        }

        var pagesTarget = FromPagesUri(uri);
        if (pagesTarget.IsValid)
            return pagesTarget;

        return InvalidTarget(
            "目前支援 GitHub、GitLab、Codeberg 與 Bitbucket repository URL 或 Pages 網址。");
    }
    public static GitHubRepositoryTarget ParseRepositoryTarget(string? input, RemoteGitProvider defaultProvider)
    {
        var value = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return ParseRepositoryTarget(input);

        if (defaultProvider is RemoteGitProvider.GitHub
                or RemoteGitProvider.GitLab
                or RemoteGitProvider.Codeberg
                or RemoteGitProvider.Bitbucket
            && !value.Contains("://", StringComparison.Ordinal)
            && !GenericSshRemoteRegex().IsMatch(value)
            && !LooksLikeKnownHostPath(value)
            && !LooksLikeKnownPagesHost(value))
        {
            return FromProviderPath(defaultProvider, value);
        }

        return ParseRepositoryTarget(input);
    }
    public async Task UpdateSiteUrlAsync(
        GitHubRepositoryTarget target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.IsValid || string.IsNullOrWhiteSpace(target.PagesUrl))
            return;

        var uri = new Uri(target.PagesUrl);
        var url = $"{uri.Scheme}://{uri.Authority}";
        var root = "/";
        if (!target.IsUserOrOrganizationSite)
        {
            var path = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrEmpty(path))
                root = $"/{path}/";
        }

        await _config.UpdateSiteUrlAndRootAsync(url, root).ConfigureAwait(false);
    }

    public async Task<(bool HasAccess, string Message)> CheckPushAccessAsync(
        GitHubRepositoryTarget target,
        CancellationToken cancellationToken = default)
    {
        if (!target.IsValid || string.IsNullOrWhiteSpace(target.Owner) || string.IsNullOrWhiteSpace(target.Repository))
            return (false, target.ErrorMessage);

        if (target.Provider != RemoteGitProvider.GitHub)
        {
            var authHint = target.Provider switch
            {
                RemoteGitProvider.GitLab => "GitLab 不使用 GitHub CLI；push 會使用本機 Git Credential Manager 或 SSH。若瀏覽器帳號是 Owner 但 push 仍 403，請清除 Windows 認證管理員中的 git:https://gitlab.com，或改用有 write_repository 權限的 Personal Access Token / SSH key。",
                RemoteGitProvider.Bitbucket => "Bitbucket 不使用 GitHub CLI；HTTPS push 會使用本機 Git Credential Manager。請確認受邀使用者已接受 Bitbucket 使用者或群組邀請，且該帳號具備 repository/workspace write 權限；即使 Bitbucket 畫面顯示 Admin，push 仍取決於本機 credential。若瀏覽器已登入但 push 失敗，請清除 Windows 認證管理員中的 git:https://bitbucket.org，並改用 Bitbucket App password 或 SSH key。",
                _ => $"{target.ProviderName} 權限會在 git push 時由 Git Credential Manager 或 SSH 驗證。"
            };
            return (true, authHint);
        }

        var result = await GhAsync(
            $"api repos/{target.Owner}/{target.Repository} --jq .permissions.push",
            null,
            30_000,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return (false,
                $"無法確認 repository 權限。請確認 gh 已登入，且 repository 存在或目前帳號可存取。\n{result.CombinedOutput}");
        }

        var canPush = result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        return canPush
            ? (true, $"已確認具有 {target.Owner}/{target.Repository} 的推送權限。")
            : (false, $"目前 GitHub 登入帳號沒有 {target.Owner}/{target.Repository} 的推送權限。請由 owner 加入 collaborator，或改用有權限的帳號執行 gh auth login。");
    }

    public async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await GitAsync("--version", null, 10_000, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<bool> IsGhAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await GhAsync("--version", null, 10_000, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<GitRemoteInfo> GetInfoAsync(string sitePath, CancellationToken cancellationToken = default)
    {
        var data = new GitRemoteInfo();
        if (string.IsNullOrWhiteSpace(sitePath) || !Directory.Exists(sitePath))
            return data;

        var branch = await GitAsync("branch --show-current", sitePath, 10_000, cancellationToken).ConfigureAwait(false);
        if (branch.Success)
            data.Branch = branch.StandardOutput.Trim();

        var remote = await GitAsync("remote get-url origin", sitePath, 10_000, cancellationToken).ConfigureAwait(false);
        if (remote.Success)
        {
            data.RemoteUrl = remote.StandardOutput.Trim();
            var target = ParseRepositoryTarget(data.RemoteUrl);
            if (target.IsValid)
            {
                data.Provider = target.Provider;
                data.Owner = target.Owner;
                data.Repo = target.Repository;
            }
        }

        var auth = await GhAsync("auth status", sitePath, 15_000, cancellationToken).ConfigureAwait(false);
        data.GhAuthenticated = auth.Success
            || auth.CombinedOutput.Contains("Logged in", StringComparison.OrdinalIgnoreCase);

        var user = await GhAsync("api user --jq .login", sitePath, 15_000, cancellationToken).ConfigureAwait(false);
        if (user.Success)
            data.GhUser = user.StandardOutput.Trim();

        return data;
    }
    public async Task<CommandResult> InitRepoAsync(string sitePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Path.Combine(sitePath, ".git")))
        {
            var init = await GitAsync("init -b main", sitePath, 30_000, cancellationToken).ConfigureAwait(false);
            if (!init.Success) return init;
        }

        EnsureGitignore(sitePath);
        return CommandResult.Ok("Git repository ready.");
    }

    public void EnsureGitignore(string sitePath)
    {
        var path = Path.Combine(sitePath, ".gitignore");
        const string defaults = """
.DS_Store
Thumbs.db
db.json
*.log
node_modules/
public/
.deploy_git/
""";
        if (!File.Exists(path))
        {
            File.WriteAllText(path, defaults);
            return;
        }

        var text = File.ReadAllText(path);
        foreach (var entry in new[] { "node_modules/", "public/", ".deploy_git/" })
        {
            if (!text.Contains(entry, StringComparison.Ordinal))
                File.AppendAllText(path, Environment.NewLine + entry + Environment.NewLine);
        }
    }

    public async Task EnsureGitHubActionsWorkflowAsync(
        string sitePath,
        CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(sitePath, cancellationToken).ConfigureAwait(false);
        if (info.Provider != RemoteGitProvider.Unknown && info.Provider != RemoteGitProvider.GitHub)
            return;

        var dir = Path.Combine(sitePath, ".github", "workflows");
        Directory.CreateDirectory(dir);
        var hexoWorkflow = Path.Combine(dir, "hexo.yml");
        var legacyWorkflow = Path.Combine(dir, "pages.yml");
        if (File.Exists(hexoWorkflow) || File.Exists(legacyWorkflow))
            return;

        await File.WriteAllTextAsync(hexoWorkflow, DefaultHexoPagesWorkflow, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CommandResult> CommitAllAsync(
        string sitePath,
        string message,
        CancellationToken cancellationToken = default)
    {
        await GitAsync("add -A", sitePath, 60_000, cancellationToken).ConfigureAwait(false);
        var status = await GitAsync("status --porcelain", sitePath, 30_000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
            return CommandResult.Ok("沒有需要提交的變更。");

        Dictionary<string, string?>? env = null;
        var email = await GitAsync("config user.email", sitePath, 10_000, cancellationToken)
            .ConfigureAwait(false);
        if (!email.Success || string.IsNullOrWhiteSpace(email.StandardOutput))
        {
            env = new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = "Hexoer",
                ["GIT_AUTHOR_EMAIL"] = "hexoer@local",
                ["GIT_COMMITTER_NAME"] = "Hexoer",
                ["GIT_COMMITTER_EMAIL"] = "hexoer@local"
            };
        }

        var msg = message.Replace("\"", "'", StringComparison.Ordinal);
        return await GitAsync($"commit -m \"{msg}\"", sitePath, 60_000, cancellationToken, env)
            .ConfigureAwait(false);
    }

    public async Task<CommandResult> CreateRepoAndPushAsync(
        string sitePath,
        string repoName,
        bool isPublic = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("初始化 Git…");
        var init = await InitRepoAsync(sitePath, cancellationToken).ConfigureAwait(false);
        if (!init.Success) return init;

        FlattenNestedThemeGitRepos(sitePath, progress);

        progress?.Report("加入 GitHub Actions 工作流程…");
        await EnsureGitHubActionsWorkflowAsync(sitePath, cancellationToken).ConfigureAwait(false);

        var markerError = await PrepareDeploymentMarkerAsync(sitePath, progress, cancellationToken)
            .ConfigureAwait(false);
        if (markerError is not null) return markerError;

        progress?.Report("提交檔案…");
        await CommitAllAsync(sitePath, "Initial commit via Hexoer", cancellationToken).ConfigureAwait(false);

        var visibility = isPublic ? "public" : "private";
        progress?.Report($"建立 GitHub repository：{repoName}…");

        var remote = await GitAsync("remote get-url origin", sitePath, 10_000, cancellationToken)
            .ConfigureAwait(false);

        if (!remote.Success)
        {
            var create = await GhAsync(
                $"repo create \"{repoName}\" --source=. --remote=origin --{visibility} --push",
                sitePath,
                180_000,
                cancellationToken).ConfigureAwait(false);
            if (!create.Success) return create;
        }
        else
        {
            progress?.Report("推送到 origin…");
            var push = await GitAsync("push -u origin HEAD", sitePath, 180_000, cancellationToken)
                .ConfigureAwait(false);
            if (!push.Success) return ExplainGitFailure(push);
        }

        progress?.Report("啟用 GitHub Pages（GitHub Actions）…");
        var pages = await EnablePagesFromActionsAsync(sitePath, cancellationToken).ConfigureAwait(false);
        return pages.Success
            ? CommandResult.Ok($"Repo ready.\n{pages.CombinedOutput}")
            : CommandResult.Fail(pages.StandardError, pages.ExitCode, pages.StandardOutput);
    }

    public async Task<CommandResult> ConnectExistingRepositoryAndPushAsync(
        string sitePath,
        GitHubRepositoryTarget target,
        string commitMessage,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!target.IsValid || string.IsNullOrWhiteSpace(target.CanonicalUrl))
            return CommandResult.Fail(target.ErrorMessage);

        progress?.Report("初始化本機 Git repository…");
        var init = await InitRepoAsync(sitePath, cancellationToken).ConfigureAwait(false);
        if (!init.Success) return init;

        var remote = await GitAsync("remote get-url origin", sitePath, 10_000, cancellationToken)
            .ConfigureAwait(false);
        if (remote.Success)
        {
            var existingTarget = ParseRepositoryTarget(remote.StandardOutput.Trim());
            if (!existingTarget.IsValid
                || existingTarget.Provider != target.Provider
                || !string.Equals(existingTarget.Owner, target.Owner, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existingTarget.Repository, target.Repository, StringComparison.OrdinalIgnoreCase))
            {
                var previousOrigin = remote.StandardOutput.Trim();
                progress?.Report($"重新指向 origin：{previousOrigin} → {target.CanonicalUrl}…");
                var setRemote = await GitAsync(
                    $"remote set-url origin \"{target.CanonicalUrl}\"",
                    sitePath,
                    15_000,
                    cancellationToken).ConfigureAwait(false);
                if (!setRemote.Success) return setRemote;
            }
        }
        else
        {
            progress?.Report($"連結 origin：{target.ProviderName} {target.Owner}/{target.Repository}…");
            var addRemote = await GitAsync(
                $"remote add origin \"{target.CanonicalUrl}\"",
                sitePath,
                15_000,
                cancellationToken).ConfigureAwait(false);
            if (!addRemote.Success) return addRemote;
        }

        progress?.Report("抓取遠端預設分支…");
        var fetch = await GitAsync("fetch origin --prune", sitePath, 120_000, cancellationToken)
            .ConfigureAwait(false);
        if (!fetch.Success) return fetch;

        var remoteHead = await GitAsync("ls-remote --symref origin HEAD", sitePath, 30_000, cancellationToken)
            .ConfigureAwait(false);
        var branchMatch = RemoteHeadRegex().Match(remoteHead.StandardOutput);
        var remoteBranch = branchMatch.Success ? branchMatch.Groups["branch"].Value : "main";
        if (!GitBranchRegex().IsMatch(remoteBranch))
            return CommandResult.Fail("遠端預設分支名稱格式不安全，已停止操作。");
        var remoteHasCommit = RemoteHeadCommitRegex().IsMatch(remoteHead.StandardOutput);

        await PrepareReadmeForRemoteMergeAsync(sitePath, remoteBranch, progress, cancellationToken)
            .ConfigureAwait(false);

        var localHead = await GitAsync("rev-parse --verify HEAD", sitePath, 10_000, cancellationToken)
            .ConfigureAwait(false);
        if (remoteHasCommit && !localHead.Success)
        {
            progress?.Report($"以遠端 {remoteBranch} 為基準，保留本機未追蹤網站檔案…");
            var checkout = await GitAsync(
                $"checkout -B \"{remoteBranch}\" --track \"origin/{remoteBranch}\"",
                sitePath,
                30_000,
                cancellationToken).ConfigureAwait(false);
            if (!checkout.Success)
            {
                return CommandResult.Fail(
                    $"遠端檔案與本機未追蹤檔案衝突，已停止連結；沒有強制覆蓋。\n{checkout.CombinedOutput}",
                    checkout.ExitCode,
                    checkout.StandardOutput);
            }
        }
        else if (remoteHasCommit && localHead.Success)
        {
            var merge = await MergeOriginBranchAsync(sitePath, remoteBranch, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!merge.Success) return merge;
        }

        FlattenNestedThemeGitRepos(sitePath, progress);

        progress?.Report(target.Provider == RemoteGitProvider.GitHub
            ? "加入 GitHub Actions workflow 並提交網站…"
            : "提交網站變更…");
        if (target.Provider == RemoteGitProvider.GitHub)
            await EnsureGitHubActionsWorkflowAsync(sitePath, cancellationToken).ConfigureAwait(false);

        var markerError = await PrepareDeploymentMarkerAsync(sitePath, progress, cancellationToken)
            .ConfigureAwait(false);
        if (markerError is not null) return markerError;
        var commit = await CommitAllAsync(sitePath, commitMessage, cancellationToken).ConfigureAwait(false);
        if (!commit.Success) return commit;

        progress?.Report($"推送到 {target.ProviderName} {target.Owner}/{target.Repository}…");
        var push = await GitAsync(
            $"push -u origin HEAD:\"{remoteBranch}\"",
            sitePath,
            180_000,
            cancellationToken).ConfigureAwait(false);
        if (!push.Success && IsNonFastForwardRejection(push.CombinedOutput))
        {
            var sync = await SyncOriginBranchBeforePushAsync(sitePath, remoteBranch, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!sync.Success) return sync;

            progress?.Report($"重新推送到 {target.ProviderName} {target.Owner}/{target.Repository}…");
            push = await GitAsync(
                $"push -u origin HEAD:\"{remoteBranch}\"",
                sitePath,
                180_000,
                cancellationToken).ConfigureAwait(false);
        }
        if (!push.Success) return ExplainGitFailure(push);

        if (target.Provider == RemoteGitProvider.GitHub)
        {
            progress?.Report("啟用 GitHub Pages（Actions）…");
            return await EnablePagesFromActionsAsync(sitePath, cancellationToken).ConfigureAwait(false);
        }

        return CommandResult.Ok($"已推送到 {target.ProviderName}。若要上線 Pages，請在 {target.ProviderName} 設定 Pages/CI，或使用 hexo-deployer-git 部署分支。");
    }
    public async Task<CommandResult> PushAsync(
        string sitePath,
        string commitMessage = "Update site via Hexoer",
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FlattenNestedThemeGitRepos(sitePath, progress);
        progress?.Report("提交變更…");
        await EnsureGitHubActionsWorkflowAsync(sitePath, cancellationToken).ConfigureAwait(false);
        var markerError = await PrepareDeploymentMarkerAsync(sitePath, progress, cancellationToken)
            .ConfigureAwait(false);
        if (markerError is not null) return markerError;
        var commit = await CommitAllAsync(sitePath, commitMessage, cancellationToken).ConfigureAwait(false);
        progress?.Report(commit.CombinedOutput);

        progress?.Report("git push…");
        var push = await GitAsync("push", sitePath, 180_000, cancellationToken).ConfigureAwait(false);
        if (!push.Success && IsNonFastForwardRejection(push.CombinedOutput))
        {
            var branch = await GitAsync("branch --show-current", sitePath, 10_000, cancellationToken)
                .ConfigureAwait(false);
            var branchName = branch.StandardOutput.Trim();
            if (!branch.Success || !GitBranchRegex().IsMatch(branchName))
                return ExplainGitFailure(push);

            var sync = await SyncOriginBranchBeforePushAsync(sitePath, branchName, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!sync.Success) return sync;

            progress?.Report("重新 git push…");
            push = await GitAsync("push", sitePath, 180_000, cancellationToken).ConfigureAwait(false);
        }
        return push.Success ? push : ExplainGitFailure(push);
    }

    public async Task<CommandResult> EnablePagesFromActionsAsync(
        string sitePath,
        CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(sitePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(info.Owner) || string.IsNullOrWhiteSpace(info.Repo))
            return CommandResult.Fail("找不到 remote（origin）。請先建立或連結 repository。");

        if (info.Provider != RemoteGitProvider.GitHub)
            return CommandResult.Ok($"已推送到 {ProviderName(info.Provider)}。Pages 啟用與 CI 設定需在該平台完成。");

        var permission = await GhAsync(
            $"api repos/{info.Owner}/{info.Repo} --jq .permissions.admin",
            sitePath,
            30_000,
            cancellationToken).ConfigureAwait(false);

        if (!permission.Success)
        {
            return CommandResult.Fail(
                $"無法確認 GitHub Pages 管理權限。請確認 gh 已登入且 repository 可存取。\n{permission.CombinedOutput}",
                permission.ExitCode,
                permission.StandardOutput);
        }

        var canManagePages = permission.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!canManagePages)
        {
            return new CommandResult
            {
                ExitCode = -1,
                StandardOutput = "網站檔案與 GitHub Actions workflow 已成功推送。",
                StandardError =
                    $"目前登入帳號具有 {info.Owner}/{info.Repo} 的推送權限，但沒有管理 GitHub Pages 設定所需的 admin 權限。\n" +
                    "請 Repository 擁有者開啟 Settings > Pages，在 Build and deployment 的 Source 選擇 GitHub Actions；完成後回到 Hexoer 按「查詢 Pages 狀態」。"
            };
        }

        var current = await GhAsync(
            $"api repos/{info.Owner}/{info.Repo}/pages",
            sitePath,
            30_000,
            cancellationToken).ConfigureAwait(false);

        var pagesExist = current.Success;
        if (!pagesExist
            && !current.CombinedOutput.Contains("404", StringComparison.OrdinalIgnoreCase)
            && !current.CombinedOutput.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        var method = pagesExist ? "PUT" : "POST";
        var update = await GhAsync(
            $"api -X {method} repos/{info.Owner}/{info.Repo}/pages -f build_type=workflow",
            sitePath,
            60_000,
            cancellationToken).ConfigureAwait(false);

        if (update.Success)
        {
            return CommandResult.Ok(pagesExist
                ? "已將 GitHub Pages 建置來源更新為 GitHub Actions。"
                : "已啟用 GitHub Pages（GitHub Actions）。");
        }

        return CommandResult.Fail(
            $"無法將 GitHub Pages 設為 GitHub Actions。\n{update.CombinedOutput}",
            update.ExitCode,
            update.StandardOutput);
    }
    public async Task<GitHubPagesStatus> GetPagesStatusAsync(
        string sitePath,
        string? owner = null,
        string? repo = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            var info = await GetInfoAsync(sitePath, cancellationToken).ConfigureAwait(false);
            owner ??= info.Owner;
            repo ??= info.Repo;
            if (info.Provider != RemoteGitProvider.Unknown && info.Provider != RemoteGitProvider.GitHub)
            {
                var target = FromProviderPath(info.Provider, $"{info.Owner}/{info.Repo}");
                return new GitHubPagesStatus
                {
                    Success = true,
                    Enabled = false,
                    HtmlUrl = target.PagesUrl,
                    Status = "external_provider",
                    Message = $"{target.ProviderName} Pages 狀態需在平台上查詢；Hexoer 已提供建議網址。"
                };
            }
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return new GitHubPagesStatus
            {
                Success = false,
                Enabled = false,
                Message = "尚未連結 repository。"
            };
        }

        var result = await GhAsync(
            $"api repos/{owner}/{repo}/pages",
            sitePath,
            30_000,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.CombinedOutput.Contains("404", StringComparison.OrdinalIgnoreCase)
                || result.CombinedOutput.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
            {
                return new GitHubPagesStatus
                {
                    Success = false,
                    Enabled = false,
                    Status = "not_found",
                    Message = "GitHub Pages 尚未啟用。"
                };
            }

            return new GitHubPagesStatus
            {
                Success = false,
                Enabled = false,
                Message = result.CombinedOutput
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            var cname = root.TryGetProperty("cname", out var c) && c.ValueKind != JsonValueKind.Null
                ? c.GetString()
                : null;
            var buildType = root.TryGetProperty("build_type", out var b) ? b.GetString() : null;

            string? branch = null;
            string? path = null;
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            {
                branch = source.TryGetProperty("branch", out var br) ? br.GetString() : null;
                path = source.TryGetProperty("path", out var p) ? p.GetString() : null;
            }

            DateTime? updated = null;
            if (root.TryGetProperty("updated_at", out var ua) && ua.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ua.GetString(), out var dt))
            {
                updated = dt.ToLocalTime();
            }

            return new GitHubPagesStatus
            {
                Success = true,
                Enabled = true,
                Status = status,
                HtmlUrl = htmlUrl,
                SourceBranch = branch,
                SourcePath = path,
                BuildType = buildType,
                Cname = cname,
                UpdatedAt = updated,
                Message = status switch
                {
                    "built" => "網站已成功建置並上線。",
                    "building" => "正在建置中…",
                    "errored" => "建置發生錯誤，請檢查 Actions 日誌。",
                    null => "GitHub Pages 已啟用。",
                    _ => $"狀態：{status}"
                }
            };
        }
        catch (Exception ex)
        {
            return new GitHubPagesStatus
            {
                Success = false,
                Enabled = true,
                Message = $"無法解析 Pages 回應：{ex.Message}\n{result.StandardOutput}"
            };
        }
    }

    public async Task<CommandResult> OpenGhAuthLoginAsync(CancellationToken cancellationToken = default)
    {
        return await GhAsync("auth login --web --git-protocol https", null, 300_000, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ConfigureDeployAsync(string repoUrl, string branch = "gh-pages", CancellationToken ct = default)
    {
        await _config.ConfigureGitDeployAsync(repoUrl, branch).ConfigureAwait(false);
        await _hexo.InstallDeployerGitAsync(ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> DeployAsync(CancellationToken ct = default)
    {
        await _hexo.GenerateAsync(ct).ConfigureAwait(false);
        return await _hexo.DeployAsync(ct).ConfigureAwait(false);
    }

    public async Task<(string? Owner, string? Repo)> TryParseRepoFromConfigAsync()
    {
        var (repoUrl, _) = await _config.ReadDeploySettingsAsync().ConfigureAwait(false);
        var target = ParseRepositoryTarget(repoUrl);
        return target.IsValid ? (target.Owner, target.Repository) : ParseOwnerRepo(repoUrl);
    }

    public static (string? Owner, string? Repo) ParseOwnerRepo(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            return (null, null);

        var target = ParseRepositoryTarget(repoUrl);
        return target.IsValid ? (target.Owner, target.Repository) : (null, null);
    }
    private async Task<CommandResult?> PrepareDeploymentMarkerAsync(
        string sitePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var marker = await _deploymentMonitor.PrepareDeploymentAsync(sitePath, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report($"建立部署版本標記：{marker.DeploymentId}");
            return null;
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"無法建立部署版本標記，已停止提交與推送：{ex.Message}");
        }
    }

    private static void FlattenNestedThemeGitRepos(string sitePath, IProgress<string>? progress)
    {
        var themesDir = Path.Combine(sitePath, "themes");
        if (!Directory.Exists(themesDir)) return;

        foreach (var themeDir in Directory.GetDirectories(themesDir))
        {
            var gitDir = Path.Combine(themeDir, ".git");
            try
            {
                if (Directory.Exists(gitDir))
                {
                    progress?.Report($"將主題 {Path.GetFileName(themeDir)} 納入網站 repository（移除巢狀 .git）…");
                    Directory.Delete(gitDir, recursive: true);
                }
                else if (File.Exists(gitDir))
                {
                    progress?.Report($"將主題 {Path.GetFileName(themeDir)} 納入網站 repository（移除巢狀 .git）…");
                    File.Delete(gitDir);
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"無法移除 {Path.GetFileName(themeDir)} 的巢狀 .git：{ex.Message}");
            }
        }
    }

    private async Task<CommandResult> MergeOriginBranchAsync(
        string sitePath,
        string branch,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"合併遠端 {branch}（允許初始 README 歷史）…");
        var merge = await GitAsync(
            $"merge \"origin/{branch}\" --allow-unrelated-histories --no-edit",
            sitePath,
            60_000,
            cancellationToken).ConfigureAwait(false);
        if (merge.Success) return merge;

        var readmeResolution = await TryResolveReadmeOnlyMergeConflictAsync(
            sitePath,
            merge,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (readmeResolution is not null) return readmeResolution;

        await GitAsync("merge --abort", sitePath, 15_000, cancellationToken).ConfigureAwait(false);
        return CommandResult.Fail(
            $"遠端內容與本機內容發生合併衝突，已中止合併且未推送。\n{merge.CombinedOutput}",
            merge.ExitCode,
            merge.StandardOutput);
    }

    private async Task<CommandResult?> TryResolveReadmeOnlyMergeConflictAsync(
        string sitePath,
        CommandResult mergeResult,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!ContainsAny(mergeResult.CombinedOutput, "README.md"))
            return null;

        var conflicts = await GitAsync("diff --name-only --diff-filter=U", sitePath, 15_000, cancellationToken)
            .ConfigureAwait(false);
        if (!conflicts.Success)
            return null;

        var conflictedFiles = conflicts.StandardOutput
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
        if (conflictedFiles.Length != 1 || !conflictedFiles[0].Equals("README.md", StringComparison.OrdinalIgnoreCase))
            return null;

        progress?.Report("偵測到平台初始 README 與本機 README 衝突；保留遠端 README，並將本機內容另存為 README.hexo.md…");
        string? backupFileName = null;
        var localReadme = await GitAsync("show HEAD:README.md", sitePath, 15_000, cancellationToken)
            .ConfigureAwait(false);
        if (localReadme.Success && !string.IsNullOrWhiteSpace(localReadme.StandardOutput))
        {
            var backupPath = GetAvailableReadmeBackupPath(sitePath);
            await File.WriteAllTextAsync(backupPath, localReadme.StandardOutput, cancellationToken)
                .ConfigureAwait(false);
            backupFileName = Path.GetFileName(backupPath);
        }

        var checkoutRemoteReadme = await GitAsync("checkout --theirs -- README.md", sitePath, 15_000, cancellationToken)
            .ConfigureAwait(false);
        if (!checkoutRemoteReadme.Success)
            return null;

        var addArguments = backupFileName is null
            ? "add -- README.md"
            : $"add -- README.md \"{backupFileName}\"";
        var add = await GitAsync(addArguments, sitePath, 15_000, cancellationToken)
            .ConfigureAwait(false);
        if (!add.Success)
            return null;

        var commit = await GitAsync("commit --no-edit", sitePath, 60_000, cancellationToken)
            .ConfigureAwait(false);
        return commit.Success
            ? commit
            : CommandResult.Fail(
                $"已處理 README 衝突，但無法完成合併提交。\n{commit.CombinedOutput}",
                commit.ExitCode,
                commit.StandardOutput);
    }

    private static string GetAvailableReadmeBackupPath(string sitePath)
    {
        var first = Path.Combine(sitePath, "README.hexo.md");
        if (!File.Exists(first)) return first;

        for (var index = 2; index < 100; index++)
        {
            var candidate = Path.Combine(sitePath, $"README.hexo-{index}.md");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(sitePath, $"README.hexo-{DateTimeOffset.Now:yyyyMMddHHmmss}.md");
    }

    private async Task<CommandResult> SyncOriginBranchBeforePushAsync(
        string sitePath,
        string branch,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!GitBranchRegex().IsMatch(branch))
            return CommandResult.Fail("遠端分支名稱格式不安全，已停止自動合併。");

        progress?.Report($"遠端 {branch} 有新提交，先抓取並安全合併…");
        var fetch = await GitAsync("fetch origin --prune", sitePath, 120_000, cancellationToken)
            .ConfigureAwait(false);
        if (!fetch.Success) return ExplainGitFailure(fetch);

        return await MergeOriginBranchAsync(sitePath, branch, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static CommandResult ExplainGitFailure(CommandResult result)
    {
        var output = result.CombinedOutput;
        if (string.IsNullOrWhiteSpace(output))
            return result;

        if (IsNonFastForwardRejection(output))
        {
            return CommandResult.Fail(
                "遠端 repository 有本機尚未合併的更新。Hexoer 已嘗試先抓取並安全合併；若仍失敗，請依日誌處理衝突後再推送。\n" + output,
                result.ExitCode,
                result.StandardOutput);
        }

        if (ContainsAny(output, "You are not allowed to push code", "requested URL returned error: 403", "HTTP 403"))
        {
            return CommandResult.Fail(
                "推送失敗：目前 Git push 使用的 GitLab 帳號或 token 沒有此專案的寫入權限。請確認它和瀏覽器登入帳號一致，並在 GitLab 將帳號加入專案或群組授予 Developer/Maintainer；或改用有 write_repository 權限的 Personal Access Token / SSH key 後重試。\n" + output,
                result.ExitCode,
                result.StandardOutput);
        }

        if (ContainsAny(output, "bitbucket.org") && ContainsAny(output, "Authentication failed", "HTTP Basic: Access denied", "could not read Username", "Invalid username or password", "403", "Forbidden"))
        {
            return CommandResult.Fail(
                "推送失敗：Bitbucket 認證或權限不足。若 Bitbucket 畫面已顯示 Admin，請確認受邀使用者已接受邀請，並清除 Windows 認證管理員中的 git:https://bitbucket.org；push 需要本機 Git 使用 Bitbucket App password 或 SSH key。\n" + output,
                result.ExitCode,
                result.StandardOutput);
        }
        if (ContainsAny(output, "Authentication failed", "HTTP Basic: Access denied", "could not read Username", "Invalid username or password"))
        {
            return CommandResult.Fail(
                "推送失敗：Git 認證失敗。請重新登入 Git Credential Manager，或改用具備寫入權限的 Personal Access Token / SSH key。\n" + output,
                result.ExitCode,
                result.StandardOutput);
        }

        return result;
    }

    private static bool IsNonFastForwardRejection(string output) =>
        ContainsAny(
            output,
            "fetch first",
            "Updates were rejected because the remote contains work that you do not have locally",
            "non-fast-forward",
            "failed to push some refs");

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
    private async Task PrepareReadmeForRemoteMergeAsync(
        string sitePath,
        string remoteBranch,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var localReadme = Path.Combine(sitePath, "README.md");
        if (!File.Exists(localReadme)) return;

        var remoteReadme = await GitAsync(
            $"show \"origin/{remoteBranch}:README.md\"",
            sitePath,
            15_000,
            cancellationToken).ConfigureAwait(false);
        if (!remoteReadme.Success) return;

        var localText = await File.ReadAllTextAsync(localReadme, cancellationToken).ConfigureAwait(false);
        if (string.Equals(NormalizeNewlines(localText), NormalizeNewlines(remoteReadme.StandardOutput), StringComparison.Ordinal))
            return;

        var backup = Path.Combine(sitePath, "README.hexo.md");
        if (!File.Exists(backup))
        {
            File.Move(localReadme, backup);
            progress?.Report("本機 README.md 與遠端不同，已改名為 README.hexo.md，避免覆蓋 GitHub 說明。");
        }
        else
        {
            File.Delete(localReadme);
            progress?.Report("已暫時移除本機 README.md，以保留遠端 GitHub README。");
        }
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private Task<CommandResult> GitAsync(
        string arguments,
        string? workingDirectory,
        int timeoutMs,
        CancellationToken cancellationToken,
        IDictionary<string, string?>? env = null) =>
        _runner.RunAsync("git", arguments, workingDirectory, cancellationToken, timeoutMs, env);

    private Task<CommandResult> GhAsync(
        string arguments,
        string? workingDirectory,
        int timeoutMs,
        CancellationToken cancellationToken) =>
        _runner.RunAsync("gh", arguments, workingDirectory, cancellationToken, timeoutMs);

    private static (string? Owner, string? Repo) ParseGitHubRemote(string url)
    {
        var target = ParseRepositoryTarget(url);
        return target.IsValid ? (target.Owner, target.Repository) : (null, null);
    }

    private static GitHubRepositoryTarget FromOwnerRepo(string owner, string repository) =>
        FromProviderPath(RemoteGitProvider.GitHub, $"{owner}/{repository}");

    private static GitHubRepositoryTarget FromProviderPath(RemoteGitProvider provider, string path)
    {
        var segments = path.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
            return InvalidTarget("網址必須包含 owner/workspace 與 repository 名稱。");

        var repository = Uri.UnescapeDataString(segments[^1]);
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];

        var owner = string.Join('/', segments[..^1].Select(Uri.UnescapeDataString));
        if (!RepositoryOwnerRegex().IsMatch(owner)
            || owner.Contains("..", StringComparison.Ordinal)
            || owner.Contains("//", StringComparison.Ordinal)
            || !GitHubRepositoryRegex().IsMatch(repository))
        {
            return InvalidTarget("owner/workspace 或 repository 名稱格式無效。");
        }

        var canonicalHost = provider switch
        {
            RemoteGitProvider.GitHub => "github.com",
            RemoteGitProvider.GitLab => "gitlab.com",
            RemoteGitProvider.Codeberg => "codeberg.org",
            RemoteGitProvider.Bitbucket => "bitbucket.org",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(canonicalHost))
            return InvalidTarget("目前支援 GitHub、GitLab、Codeberg 與 Bitbucket。");

        var pagesOwner = owner.Split('/', StringSplitOptions.RemoveEmptyEntries).Last().ToLowerInvariant();
        var repositoryLower = repository.ToLowerInvariant();
        var userSite = provider switch
        {
            RemoteGitProvider.GitHub => repository.Equals($"{pagesOwner}.github.io", StringComparison.OrdinalIgnoreCase),
            RemoteGitProvider.GitLab => repository.Equals($"{pagesOwner}.gitlab.io", StringComparison.OrdinalIgnoreCase),
            RemoteGitProvider.Codeberg => repository.Equals("pages", StringComparison.OrdinalIgnoreCase),
            RemoteGitProvider.Bitbucket => repository.Equals($"{pagesOwner}.bitbucket.io", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        var pagesUrl = provider switch
        {
            RemoteGitProvider.GitHub => userSite
                ? $"https://{pagesOwner}.github.io/"
                : $"https://{pagesOwner}.github.io/{repository}/",
            RemoteGitProvider.GitLab => userSite
                ? $"https://{pagesOwner}.gitlab.io/"
                : $"https://{pagesOwner}.gitlab.io/{repository}/",
            RemoteGitProvider.Codeberg => userSite
                ? $"https://{pagesOwner}.codeberg.page/"
                : $"https://{pagesOwner}.codeberg.page/{repository}/",
            RemoteGitProvider.Bitbucket => userSite
                ? $"https://{pagesOwner}.bitbucket.io/"
                : $"https://{pagesOwner}.bitbucket.io/{repository}/",
            _ => null
        };

        return new GitHubRepositoryTarget
        {
            IsValid = true,
            Provider = provider,
            Owner = owner,
            Repository = repository,
            CanonicalUrl = $"https://{canonicalHost}/{owner}/{repository}.git",
            PagesUrl = pagesUrl,
            IsUserOrOrganizationSite = userSite
        };
    }

    private static GitHubRepositoryTarget FromPagesUri(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase))
        {
            var owner = host[..^".github.io".Length];
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var repository = segments.Length == 0 || IsPublishedUserSitePath(segments)
                ? $"{owner}.github.io"
                : Uri.UnescapeDataString(segments[0]);
            return FromProviderPath(RemoteGitProvider.GitHub, $"{owner}/{repository}");
        }

        if (host.EndsWith(".gitlab.io", StringComparison.OrdinalIgnoreCase))
        {
            var owner = host[..^".gitlab.io".Length];
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var repository = segments.Length == 0 || IsPublishedUserSitePath(segments)
                ? $"{owner}.gitlab.io"
                : Uri.UnescapeDataString(segments[0]);
            return FromProviderPath(RemoteGitProvider.GitLab, $"{owner}/{repository}");
        }

        if (host.EndsWith(".codeberg.page", StringComparison.OrdinalIgnoreCase))
        {
            var owner = host[..^".codeberg.page".Length];
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var repository = segments.Length == 0 || IsPublishedUserSitePath(segments)
                ? "pages"
                : Uri.UnescapeDataString(segments[0]);
            return FromProviderPath(RemoteGitProvider.Codeberg, $"{owner}/{repository}");
        }

        if (host.EndsWith(".bitbucket.io", StringComparison.OrdinalIgnoreCase))
        {
            var owner = host[..^".bitbucket.io".Length];
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var repository = segments.Length == 0 || IsPublishedUserSitePath(segments)
                ? $"{owner}.bitbucket.io"
                : Uri.UnescapeDataString(segments[0]);
            return FromProviderPath(RemoteGitProvider.Bitbucket, $"{owner}/{repository}");
        }

        return InvalidTarget("不是支援的 Pages 網址。");
    }

    private static bool LooksLikeKnownHostPath(string value) =>
        value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("www.github.com/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("gitlab.com/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("www.gitlab.com/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("codeberg.org/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("www.codeberg.org/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("bitbucket.org/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("www.bitbucket.org/", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeKnownPagesHost(string value) =>
        value.Contains(".github.io", StringComparison.OrdinalIgnoreCase)
        || value.Contains(".gitlab.io", StringComparison.OrdinalIgnoreCase)
        || value.Contains(".codeberg.page", StringComparison.OrdinalIgnoreCase)
        || value.Contains(".bitbucket.io", StringComparison.OrdinalIgnoreCase);

    private static RemoteGitProvider ProviderFromHost(string host)
    {
        var normalized = host.Trim().ToLowerInvariant();
        if (normalized is "github.com" or "www.github.com") return RemoteGitProvider.GitHub;
        if (normalized is "gitlab.com" or "www.gitlab.com") return RemoteGitProvider.GitLab;
        if (normalized is "codeberg.org" or "www.codeberg.org") return RemoteGitProvider.Codeberg;
        if (normalized is "bitbucket.org" or "www.bitbucket.org") return RemoteGitProvider.Bitbucket;
        return RemoteGitProvider.Unknown;
    }

    private static string ProviderName(RemoteGitProvider provider) => provider switch
    {
        RemoteGitProvider.GitHub => "GitHub",
        RemoteGitProvider.GitLab => "GitLab",
        RemoteGitProvider.Codeberg => "Codeberg",
        RemoteGitProvider.Bitbucket => "Bitbucket",
        _ => "Git"
    };

    private static GitHubRepositoryTarget InvalidTarget(string message) => new()
    {
        IsValid = false,
        ErrorMessage = message
    };
    private const string DefaultHexoPagesWorkflow = """
# Build and deploy a Hexo site to GitHub Pages
name: Deploy Hexo site to Pages

on:
  push:
    branches: ["main", "master"]
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: "pages"
  cancel-in-progress: false

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          submodules: recursive
          fetch-depth: 0

      - name: Setup Node
        uses: actions/setup-node@v4
        with:
          node-version: "22"
          cache: npm

      - name: Install dependencies
        run: |
          if [ -f package-lock.json ]; then npm ci; else npm install; fi

      - name: Setup Pages
        id: pages
        uses: actions/configure-pages@v5

      - name: Build with Hexo
        env:
          TZ: Asia/Taipei
        run: npx hexo generate

      - name: Upload artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: ./public

  deploy:
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    runs-on: ubuntu-latest
    needs: build
    steps:
      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
""";

    private static bool IsPublishedUserSitePath(string[] segments)
    {
        if (segments.Length == 0) return true;

        var first = Uri.UnescapeDataString(segments[0]);
        if (first.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || first.Equals("index.htm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (PublishedUserSiteRootRegex().IsMatch(first) || YearSegmentRegex().IsMatch(first))
            return true;

        return segments.Length >= 2;
    }

    [GeneratedRegex(@"^(?:git@)?(?<host>github\.com|gitlab\.com|codeberg\.org|bitbucket\.org):(?<path>[^\s]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSshRemoteRegex();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._/-]{0,200}[A-Za-z0-9])?$")]
    private static partial Regex RepositoryOwnerRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex GitHubRepositoryRegex();

    [GeneratedRegex(@"ref:\s+refs/heads/(?<branch>[^\s]+)\s+HEAD")]
    private static partial Regex RemoteHeadRegex();

    [GeneratedRegex(@"(?m)^[0-9a-f]{40,64}\s+HEAD$")]
    private static partial Regex RemoteHeadCommitRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex GitBranchRegex();

[GeneratedRegex(@"^(archives|tags|categories|about|css|js|images|img|assets|fancybox|lib|page|atom\.xml|rss\.xml|sitemap\.xml|404\.html|favicon\.ico)$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedUserSiteRootRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearSegmentRegex();
}
