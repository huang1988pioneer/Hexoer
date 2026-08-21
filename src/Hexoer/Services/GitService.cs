using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

/// <summary>Uses the system Git and GitHub CLI; credentials stay with those tools.</summary>
public sealed class GitService
{
    private readonly ProcessRunner _runner;
    private readonly ProjectContext _context;

    public GitService(ProcessRunner runner, ProjectContext context)
    {
        _runner = runner;
        _context = context;
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

        var directory = _context.ProjectPath!;
        var init = await _runner.RunShellAsync("git init", directory, ct).ConfigureAwait(false);
        if (!init.Success) return init;

        var branch = await _runner.RunShellAsync("git branch -M main", directory, ct).ConfigureAwait(false);
        if (!branch.Success) return branch;

        var remote = await _runner.RunShellAsync("git remote get-url origin", directory, ct).ConfigureAwait(false);
        var remoteCommand = remote.Success
            ? $"git remote set-url origin {Quote(remoteUrl)}"
            : $"git remote add origin {Quote(remoteUrl)}";
        var configured = await _runner.RunShellAsync(remoteCommand, directory, ct).ConfigureAwait(false);
        if (!configured.Success) return configured;

        var add = await _runner.RunShellAsync("git add .", directory, ct).ConfigureAwait(false);
        if (!add.Success) return add;

        var commit = await _runner.RunShellAsync("git commit -m \"Initial Hexo site\"", directory, ct).ConfigureAwait(false);
        if (!commit.Success && !commit.CombinedOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            return commit;

        return await _runner.RunShellAsync("git push -u origin main", directory, ct).ConfigureAwait(false);
    }

    public async Task WritePagesWorkflowAsync(CancellationToken ct = default)
    {
        EnsureProject();
        var directory = Path.Combine(_context.ProjectPath!, ".github", "workflows");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pages.yml");
        await File.WriteAllTextAsync(path, PagesWorkflow, ct).ConfigureAwait(false);
    }

    private void EnsureProject()
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案資料夾。");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private const string PagesWorkflow = """
name: Deploy Hexo to GitHub Pages

on:
  push:
    branches: [main]
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: true

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive
      - uses: actions/setup-node@v4
        with:
          node-version: "20"
          cache: npm
      - run: npm ci
      - run: npm run build
      - uses: actions/upload-pages-artifact@v3
        with:
          path: ./public
  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - id: deployment
        uses: actions/deploy-pages@v4
""";
}
