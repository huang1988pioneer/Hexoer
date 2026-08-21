using System;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

/// <summary>Uses the system Git and GitHub CLI; credentials stay with those tools.</summary>
public sealed class GitService
{
    private readonly ProcessRunner _runner;
    private readonly ProjectContext _context;
    private readonly GitHubService _github;

    public GitService(ProcessRunner runner, ProjectContext context, GitHubService github)
    {
        _runner = runner;
        _context = context;
        _github = github;
    }

    public async Task<(string? UserName, string? Email, bool GitHubAuthenticated)> GetIdentityAsync(CancellationToken ct = default)
    {
        var name = await _runner.RunShellAsync("git config --global user.name", cancellationToken: ct).ConfigureAwait(false);
        var email = await _runner.RunShellAsync("git config --global user.email", cancellationToken: ct).ConfigureAwait(false);
        var github = await _runner.RunShellAsync("gh auth status -h github.com", cancellationToken: ct).ConfigureAwait(false);
        return (Clean(name.StandardOutput), Clean(email.StandardOutput), github.Success);
    }

    public async Task<CommandResult> InitializeAndPushAsync(string remoteUrl, CancellationToken ct = default)
    {
        EnsureProject();
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new ArgumentException("請提供 GitHub repository URL。", nameof(remoteUrl));

        var target = GitHubService.ParseRepositoryTarget(remoteUrl);
        if (!target.IsValid)
            return CommandResult.Fail(target.ErrorMessage);

        return await _github.ConnectExistingRepositoryAndPushAsync(
            _context.ProjectPath!,
            target,
            "Initial Hexo site via Hexoer",
            progress: null,
            cancellationToken: ct).ConfigureAwait(false);
    }

    public Task WritePagesWorkflowAsync(CancellationToken ct = default)
    {
        EnsureProject();
        return _github.EnsureGitHubActionsWorkflowAsync(_context.ProjectPath!, ct);
    }

    private void EnsureProject()
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案資料夾。");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
