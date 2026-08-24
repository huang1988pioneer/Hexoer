using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

public sealed partial class GitHubService
{
    private static readonly string[] HexoSourceBranchCandidates = ["source", "hexo", "main", "master"];
    private static readonly string[] IgnoredCloneDirectoryEntries = ["Thumbs.db", "desktop.ini", ".DS_Store"];

    public static bool DirectoryLooksLikeHexoSite(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && (File.Exists(Path.Combine(path, "_config.yml"))
            || File.Exists(Path.Combine(path, "_config.yaml")));

    public static bool DirectoryLooksLikeGeneratedSite(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(path, "index.html"))
        && !DirectoryLooksLikeHexoSite(path);

    public static string? ResolveCloneDestination(string? preferredPath, string repositoryName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(preferredPath))
        {
            error = "請選擇要複製到的本機資料夾。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(repositoryName) || !GitHubRepositoryRegex().IsMatch(repositoryName))
        {
            error = "無法決定本機資料夾名稱。";
            return null;
        }

        string preferred;
        try
        {
            preferred = Path.GetFullPath(preferredPath.Trim());
        }
        catch (Exception ex)
        {
            error = "本機路徑無效：" + ex.Message;
            return null;
        }

        if (!Directory.Exists(preferred))
            return preferred;

        if (IsEffectivelyEmpty(preferred)
            || Directory.Exists(Path.Combine(preferred, ".git"))
            || DirectoryLooksLikeHexoSite(preferred))
        {
            return preferred;
        }

        var nested = Path.GetFullPath(Path.Combine(preferred, repositoryName));
        if (!Directory.Exists(nested)
            || IsEffectivelyEmpty(nested)
            || Directory.Exists(Path.Combine(nested, ".git"))
            || DirectoryLooksLikeHexoSite(nested))
        {
            return nested;
        }

        error = $"目標資料夾已有內容：{nested}。請選擇空資料夾，以免覆蓋既有檔案。";
        return null;
    }

    public async Task<IReadOnlyList<GitHubRepositoryTarget>> ListLikelyPagesRepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var found = new List<GitHubRepositoryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var user = await GhAsync("api user --jq .login", null, 15_000, cancellationToken)
            .ConfigureAwait(false);
        var login = user.Success ? user.StandardOutput.Trim() : null;
        if (!string.IsNullOrWhiteSpace(login))
        {
            var userSite = FromOwnerRepo(login, $"{login}.github.io");
            if (userSite.IsValid)
            {
                var exists = await GhAsync(
                    $"api repos/{login}/{login}.github.io --jq .full_name",
                    null,
                    20_000,
                    cancellationToken).ConfigureAwait(false);
                if (exists.Success)
                    AddUnique(found, seen, userSite);
            }
        }

        var list = await GhAsync(
            "repo list --limit 100 --json name,nameWithOwner",
            null,
            45_000,
            cancellationToken).ConfigureAwait(false);
        if (!list.Success || string.IsNullOrWhiteSpace(list.StandardOutput))
            return found;

        try
        {
            using var doc = JsonDocument.Parse(list.StandardOutput);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var full = item.TryGetProperty("nameWithOwner", out var fullEl) ? fullEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)
                    || !name.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(full))
                {
                    continue;
                }

                var target = ParseRepositoryTarget(full);
                if (target.IsValid)
                    AddUnique(found, seen, target);
            }
        }
        catch (JsonException)
        {
            // Keep any user-site match already collected.
        }

        return found;
    }

    public async Task<RemoteHexoSiteInfo> InspectRemoteSiteAsync(
        GitHubRepositoryTarget target,
        CancellationToken cancellationToken = default)
    {
        if (!target.IsValid || string.IsNullOrWhiteSpace(target.Owner) || string.IsNullOrWhiteSpace(target.Repository))
        {
            return new RemoteHexoSiteInfo
            {
                Success = false,
                Target = target,
                Message = target.ErrorMessage
            };
        }

        if (target.Provider != RemoteGitProvider.GitHub)
        {
            var remoteHead = await GitAsync($"ls-remote --symref {Quote(target.CanonicalUrl!)} HEAD", null, 30_000, cancellationToken)
                .ConfigureAwait(false);
            if (!remoteHead.Success)
            {
                return new RemoteHexoSiteInfo
                {
                    Success = false,
                    Target = target,
                    Message = $"無法讀取 {target.ProviderName} repository。請確認網址、權限與 Git Credential Manager / SSH 設定。\n{remoteHead.CombinedOutput}"
                };
            }

            var branchMatch = RemoteHeadRegex().Match(remoteHead.StandardOutput);
            var externalDefaultBranch = branchMatch.Success ? branchMatch.Groups["branch"].Value : "main";
            return new RemoteHexoSiteInfo
            {
                Success = true,
                Target = target,
                RepositoryExists = true,
                DefaultBranch = externalDefaultBranch,
                PagesUrl = target.PagesUrl,
                Message =
                    $"Repository：{target.ProviderName} {target.Owner}/{target.Repository}\n" +
                    $"預設分支：{externalDefaultBranch}\n" +
                    $"建議 Pages 網址：{target.PagesUrl}\n" +
                    $"Hexoer 會使用 git clone 複製；{target.ProviderName} Pages 狀態需在平台上查詢。"
            };
        }

        var ghOk = await IsGhAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (!ghOk)
        {
            return new RemoteHexoSiteInfo
            {
                Success = true,
                Target = target,
                UsedGitHubCli = false,
                Message = "未偵測到 GitHub CLI。複製 GitHub repository 時會使用 git clone 預設分支；私有 repository 請先安裝並登入 gh，或確認 Git 認證可用。"
            };
        }

        var defaultBranchResult = await GhAsync(
            $"api repos/{target.Owner}/{target.Repository} --jq .default_branch",
            null,
            30_000,
            cancellationToken).ConfigureAwait(false);
        if (!defaultBranchResult.Success)
        {
            return new RemoteHexoSiteInfo
            {
                Success = false,
                Target = target,
                UsedGitHubCli = true,
                Message = "找不到 repository 或目前帳號沒有存取權限。私有網站請先執行 gh auth login。\n"
                          + defaultBranchResult.CombinedOutput
            };
        }

        var defaultBranch = string.IsNullOrWhiteSpace(defaultBranchResult.StandardOutput)
            ? "main"
            : defaultBranchResult.StandardOutput.Trim().Trim('"');
        var privateResult = await GhAsync(
            $"api repos/{target.Owner}/{target.Repository} --jq .private",
            null,
            20_000,
            cancellationToken).ConfigureAwait(false);
        var isPrivate = privateResult.Success
                        && privateResult.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        var pages = await GetPagesStatusAsync(
            string.Empty,
            target.Owner,
            target.Repository,
            cancellationToken).ConfigureAwait(false);

        var (hexoBranch, generated) = await DetectRemoteHexoSourceAsync(
            target.Owner,
            target.Repository,
            defaultBranch,
            cancellationToken).ConfigureAwait(false);

        var pagesLine = pages.Enabled
            ? $"GitHub Pages：已啟用（{pages.Status ?? "ready"}）{DescribeUrl(pages.HtmlUrl ?? target.PagesUrl)}"
            : "GitHub Pages：尚未啟用或無法查詢。仍可複製 repository。";

        var sourceLine = hexoBranch is not null
            ? $"Hexo 原始碼分支：{hexoBranch}（含 _config.yml）"
            : generated
                ? "遠端看起來是已建置的靜態網站，沒有 Hexo 原始碼（_config.yml）。複製後無法用 Hexoer 編輯文章。"
                : "無法確認遠端是否為 Hexo 專案；複製後會再檢查本機檔案。";

        return new RemoteHexoSiteInfo
        {
            Success = true,
            Target = target,
            UsedGitHubCli = true,
            RepositoryExists = true,
            IsPrivate = isPrivate,
            DefaultBranch = defaultBranch,
            PagesEnabled = pages.Enabled,
            PagesUrl = pages.HtmlUrl ?? target.PagesUrl,
            PagesStatus = pages.Status,
            HexoSourceBranch = hexoBranch,
            LooksLikeHexoSource = hexoBranch is not null,
            LooksLikeGeneratedSite = hexoBranch is null && generated,
            Message =
                $"Repository：{target.Owner}/{target.Repository}{(isPrivate ? "（私有）" : string.Empty)}\n" +
                $"預設分支：{defaultBranch}\n" +
                pagesLine + "\n" +
                sourceLine
        };
    }

    public async Task<CloneSiteResult> CloneSiteToLocalAsync(
        GitHubRepositoryTarget target,
        string preferredPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!target.IsValid || string.IsNullOrWhiteSpace(target.CanonicalUrl)
            || string.IsNullOrWhiteSpace(target.Owner) || string.IsNullOrWhiteSpace(target.Repository))
        {
            return FailClone(target.ErrorMessage);
        }

        if (!await IsGitAvailableAsync(cancellationToken).ConfigureAwait(false))
            return FailClone("找不到 Git。請先安裝 Git for Windows。");

        progress?.Report("檢查遠端 repository…");
        var remote = await InspectRemoteSiteAsync(target, cancellationToken).ConfigureAwait(false);
        if (!remote.Success && remote.UsedGitHubCli)
            return FailClone(remote.Message);

        if (!string.IsNullOrWhiteSpace(remote.Message))
            progress?.Report(remote.Message);

        var destination = ResolveCloneDestination(preferredPath, target.Repository, out var destError);
        if (destination is null)
            return FailClone(destError);

        progress?.Report($"本機目標：{destination}");

        var existing = await TryReuseExistingCloneAsync(destination, target, progress, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        if (Directory.Exists(destination) && !IsEffectivelyEmpty(destination))
        {
            return FailClone($"目標資料夾已有內容：{destination}。請選擇空資料夾。");
        }

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        var branch = remote.HexoSourceBranch;
        if (!string.IsNullOrWhiteSpace(branch) && !GitBranchRegex().IsMatch(branch))
            return FailClone("遠端 Hexo 原始碼分支名稱格式不安全，已停止複製。");

        progress?.Report(string.IsNullOrWhiteSpace(branch)
            ? $"複製 {target.Owner}/{target.Repository}…"
            : $"複製 {target.Owner}/{target.Repository}（分支 {branch}）…");

        var clone = await CloneRepositoryAsync(target, destination, branch, cancellationToken)
            .ConfigureAwait(false);
        if (!clone.Success)
        {
            return FailClone(
                $"複製失敗。請確認網址、權限與網路連線。\n{clone.CombinedOutput}",
                destination);
        }

        var checkedOut = await EnsureHexoSourceCheckoutAsync(destination, branch, progress, cancellationToken)
            .ConfigureAwait(false);
        return await FinalizeClonedSiteAsync(
                destination,
                checkedOut,
                reused: false,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CloneSiteResult?> TryReuseExistingCloneAsync(
        string destination,
        GitHubRepositoryTarget target,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(destination, ".git")))
            return null;

        var remote = await GitAsync("remote get-url origin", destination, 10_000, cancellationToken)
            .ConfigureAwait(false);
        if (!remote.Success)
        {
            return FailClone($"本機 {destination} 已是 Git repository，但沒有 origin，為避免混用已停止複製。");
        }

        var existingTarget = ParseRepositoryTarget(remote.StandardOutput.Trim());
        if (!existingTarget.IsValid
            || existingTarget.Provider != target.Provider
            || !string.Equals(existingTarget.Owner, target.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existingTarget.Repository, target.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return FailClone(
                $"本機 {destination} 已指向其他 repository：{remote.StandardOutput.Trim()}。請改選空資料夾。");
        }

        progress?.Report("本機已有相同 repository，改為開啟既有複本…");
        await GitAsync("fetch origin --prune", destination, 120_000, cancellationToken)
            .ConfigureAwait(false);
        var checkedOut = await EnsureHexoSourceCheckoutAsync(destination, null, progress, cancellationToken)
            .ConfigureAwait(false);
        return await FinalizeClonedSiteAsync(
                destination,
                checkedOut,
                reused: true,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CloneSiteResult> FinalizeClonedSiteAsync(
        string destination,
        string? branch,
        bool reused,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var isHexo = DirectoryLooksLikeHexoSite(destination);
        if (!isHexo)
        {
            var generated = DirectoryLooksLikeGeneratedSite(destination);
            return new CloneSiteResult
            {
                Success = true,
                LocalPath = destination,
                Branch = branch,
                LooksLikeHexoSource = false,
                ReusedExisting = reused,
                Message = generated
                    ? $"已複製到 {destination}，但這是已建置的靜態網站，沒有 Hexo 原始碼（_config.yml）。請改複製包含 source/_posts 的 repository 或 source 分支。"
                    : $"已複製到 {destination}，但未找到 _config.yml，無法當作 Hexo 專案開啟。"
            };
        }

        progress?.Report("已找到 Hexo 專案檔。");
        _context.ProjectPath = destination;

        var installed = false;
        if (File.Exists(Path.Combine(destination, "package.json")))
        {
            progress?.Report("安裝 npm 依賴…");
            var install = await _hexo.InstallDependenciesAsync(cancellationToken).ConfigureAwait(false);
            installed = install.Success;
            if (!install.Success)
            {
                return new CloneSiteResult
                {
                    Success = true,
                    LocalPath = destination,
                    Branch = branch,
                    LooksLikeHexoSource = true,
                    InstalledDependencies = false,
                    ReusedExisting = reused,
                    Message = $"網站已複製到 {destination}，但 npm install 失敗。可稍後在環境設定再安裝依賴。\n{install.CombinedOutput}"
                };
            }
        }

        var action = reused ? "已開啟本機複本" : "已從遠端複製到本機";
        var branchText = string.IsNullOrWhiteSpace(branch) ? string.Empty : $"（分支 {branch}）";
        return new CloneSiteResult
        {
            Success = true,
            LocalPath = destination,
            Branch = branch,
            LooksLikeHexoSource = true,
            InstalledDependencies = installed,
            ReusedExisting = reused,
            Message = $"{action}：{destination}{branchText}。"
                      + (installed ? " 依賴已安裝，可以開始編輯。" : string.Empty)
        };
    }

    private async Task<CommandResult> CloneRepositoryAsync(
        GitHubRepositoryTarget target,
        string destination,
        string? branch,
        CancellationToken cancellationToken)
    {
        var dest = Quote(destination);
        if (target.Provider == RemoteGitProvider.GitHub && await IsGhAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            var flags = "--recurse-submodules";
            if (!string.IsNullOrWhiteSpace(branch))
                flags += $" --branch {Quote(branch)}";

            return await GhAsync(
                $"repo clone {Quote($"{target.Owner}/{target.Repository}")} {dest} -- {flags}",
                null,
                600_000,
                cancellationToken).ConfigureAwait(false);
        }

        var args = "clone --recurse-submodules";
        if (!string.IsNullOrWhiteSpace(branch))
            args += $" --branch {Quote(branch)}";
        args += $" {Quote(target.CanonicalUrl!)} {dest}";
        return await GitAsync(args, null, 600_000, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> EnsureHexoSourceCheckoutAsync(
        string sitePath,
        string? preferredBranch,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (DirectoryLooksLikeHexoSite(sitePath))
        {
            var current = await GitAsync("branch --show-current", sitePath, 10_000, cancellationToken)
                .ConfigureAwait(false);
            return current.Success && !string.IsNullOrWhiteSpace(current.StandardOutput)
                ? current.StandardOutput.Trim()
                : preferredBranch;
        }

        var remoteBranches = await GitAsync("branch -r", sitePath, 15_000, cancellationToken)
            .ConfigureAwait(false);
        if (!remoteBranches.Success)
            return preferredBranch;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredBranch))
            candidates.Add(preferredBranch);
        candidates.AddRange(HexoSourceBranchCandidates);

        foreach (var branch in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!GitBranchRegex().IsMatch(branch))
                continue;

            var remoteRef = $"origin/{branch}";
            if (!ContainsRemoteBranch(remoteBranches.StandardOutput, remoteRef))
                continue;

            var tree = await GitAsync(
                $"ls-tree --name-only {Quote(remoteRef)}",
                sitePath,
                15_000,
                cancellationToken).ConfigureAwait(false);
            if (!tree.Success || !RootLooksLikeHexo(SplitLines(tree.StandardOutput)))
                continue;

            progress?.Report($"切換到 Hexo 原始碼分支 {branch}…");
            var checkout = await GitAsync(
                $"checkout -B {Quote(branch)} --track {Quote(remoteRef)}",
                sitePath,
                30_000,
                cancellationToken).ConfigureAwait(false);
            if (checkout.Success)
                return branch;
        }

        return preferredBranch;
    }

    private async Task<(string? HexoBranch, bool LooksGenerated)> DetectRemoteHexoSourceAsync(
        string owner,
        string repo,
        string? defaultBranch,
        CancellationToken cancellationToken)
    {
        var branches = new List<string>();
        if (!string.IsNullOrWhiteSpace(defaultBranch))
            branches.Add(defaultBranch);
        foreach (var extra in HexoSourceBranchCandidates)
        {
            if (!branches.Contains(extra, StringComparer.OrdinalIgnoreCase))
                branches.Add(extra);
        }

        var generated = false;
        foreach (var branch in branches)
        {
            if (!GitBranchRegex().IsMatch(branch))
                continue;

            var names = await ListRemoteRootNamesAsync(owner, repo, branch, cancellationToken)
                .ConfigureAwait(false);
            if (names is null)
                continue;

            if (RootLooksLikeHexo(names))
                return (branch, false);

            if (!generated && RootLooksLikeGenerated(names))
                generated = true;
        }

        return (null, generated);
    }

    private async Task<IReadOnlyList<string>?> ListRemoteRootNamesAsync(
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken)
    {
        var result = await GhAsync(
            $"api \"repos/{owner}/{repo}/contents/?ref={Uri.EscapeDataString(branch)}\" --jq \".[].name\"",
            null,
            30_000,
            cancellationToken).ConfigureAwait(false);
        return result.Success ? SplitLines(result.StandardOutput) : null;
    }

    private static bool RootLooksLikeHexo(IEnumerable<string> names) =>
        names.Any(name =>
            name.Equals("_config.yml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("_config.yaml", StringComparison.OrdinalIgnoreCase));

    private static bool RootLooksLikeGenerated(IEnumerable<string> names)
    {
        var list = names as IList<string> ?? names.ToList();
        return list.Any(name => name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
               && !RootLooksLikeHexo(list);
    }

    private static bool ContainsRemoteBranch(string branchList, string remoteRef)
    {
        foreach (var line in SplitLines(branchList))
        {
            var name = line.Trim();
            if (name.StartsWith("origin/HEAD", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals(remoteRef, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsEffectivelyEmpty(string path)
    {
        if (!Directory.Exists(path))
            return true;

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (IgnoredCloneDirectoryEntries.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static void AddUnique(
        ICollection<GitHubRepositoryTarget> found,
        ISet<string> seen,
        GitHubRepositoryTarget target)
    {
        var key = $"{target.Provider}:{target.Owner}/{target.Repository}";
        if (!seen.Add(key))
            return;
        found.Add(target);
    }

    private static string DescribeUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? string.Empty : $"\n網址：{url}";

    private static CloneSiteResult FailClone(string message, string? localPath = null) => new()
    {
        Success = false,
        LocalPath = localPath,
        Message = message
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}



