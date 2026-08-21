using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

public sealed class HexoService
{
    private readonly ProcessRunner _runner;
    private readonly ProjectContext _context;

    public HexoService(ProcessRunner runner, ProjectContext context)
    {
        _runner = runner;
        _context = context;
    }

    public event Action<string>? OutputReceived
    {
        add => _runner.OutputReceived += value;
        remove => _runner.OutputReceived -= value;
    }

    public async Task<EnvironmentStatus> CheckEnvironmentAsync(CancellationToken ct = default)
    {
        var status = new EnvironmentStatus { ProjectPath = _context.ProjectPath };

        var node = await _runner.RunShellAsync("node -v", cancellationToken: ct).ConfigureAwait(false);
        status.NodeInstalled = node.Success;
        status.NodeVersion = CleanVersion(node.StandardOutput);

        var npm = await _runner.RunShellAsync("npm -v", cancellationToken: ct).ConfigureAwait(false);
        status.NpmInstalled = npm.Success;
        status.NpmVersion = CleanVersion(npm.StandardOutput);

        var git = await _runner.RunShellAsync("git --version", cancellationToken: ct).ConfigureAwait(false);
        status.GitInstalled = git.Success;
        status.GitVersion = CleanVersion(git.StandardOutput);

        var hexo = await _runner.RunShellAsync("npx --yes hexo -v", cancellationToken: ct).ConfigureAwait(false);
        if (!hexo.Success)
            hexo = await _runner.RunShellAsync("hexo -v", cancellationToken: ct).ConfigureAwait(false);
        status.HexoCliInstalled = hexo.Success || (hexo.CombinedOutput?.Contains("hexo:", StringComparison.OrdinalIgnoreCase) ?? false);
        status.HexoVersion = ExtractHexoVersion(hexo.CombinedOutput);

        status.ProjectValid = _context.IsHexoProject;
        if (status.ProjectValid)
            status.ThemeName = await GetActiveThemeNameAsync().ConfigureAwait(false);

        return status;
    }

    public async Task<CommandResult> InitProjectAsync(string targetDir, string? siteName = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var name = string.IsNullOrWhiteSpace(siteName) ? Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : siteName;
        // hexo init <folder> creates folder if needed; use . when folder already exists
        var isEmpty = Directory.GetFileSystemEntries(targetDir).Length == 0;
        var arg = isEmpty ? "." : name!;
        var cwd = isEmpty ? targetDir : Path.GetDirectoryName(targetDir)!;
        if (!isEmpty)
        {
            // init into named subfolder under parent
            return await _runner.RunShellAsync($"npx --yes hexo-cli init {Quote(arg)}", cwd, ct).ConfigureAwait(false);
        }

        var result = await _runner.RunShellAsync("npx --yes hexo-cli init .", targetDir, ct).ConfigureAwait(false);
        if (result.Success)
            _context.ProjectPath = targetDir;
        return result;
    }

    public async Task<CommandResult> InstallDependenciesAsync(CancellationToken ct = default)
    {
        EnsureProject();
        return await _runner.RunShellAsync("npm install", _context.ProjectPath, ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> InstallDeployerGitAsync(CancellationToken ct = default)
    {
        EnsureProject();
        return await _runner.RunShellAsync("npm install hexo-deployer-git --save", _context.ProjectPath, ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> GenerateAsync(CancellationToken ct = default)
    {
        EnsureProject();
        return await RunHexoAsync("generate", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> CleanAsync(CancellationToken ct = default)
    {
        EnsureProject();
        return await RunHexoAsync("clean", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> ServerAsync(CancellationToken ct = default)
    {
        EnsureProject();
        return await RunHexoAsync("server", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> DeployAsync(CancellationToken ct = default)
    {
        EnsureProject();
        return await RunHexoAsync("deploy", ct).ConfigureAwait(false);
    }

    public async Task<CommandResult> NewPostAsync(string title, bool asDraft = false, CancellationToken ct = default)
    {
        EnsureProject();
        var layout = asDraft ? "draft" : "post";
        return await RunHexoAsync($"new {layout} {Quote(title)}", ct).ConfigureAwait(false);
    }

    public async Task<string?> GetActiveThemeNameAsync()
    {
        if (!_context.IsHexoProject || !File.Exists(_context.ConfigPath))
            return null;

        var text = await File.ReadAllTextAsync(_context.ConfigPath).ConfigureAwait(false);
        var match = Regex.Match(text, @"^\s*theme\s*:\s*([^\s#]+)", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim('"', '\'') : null;
    }

    public async Task SetActiveThemeAsync(string themeName)
    {
        EnsureProject();
        if (!File.Exists(_context.ConfigPath))
            throw new InvalidOperationException("_config.yml not found.");

        var text = await File.ReadAllTextAsync(_context.ConfigPath).ConfigureAwait(false);
        if (Regex.IsMatch(text, @"^\s*theme\s*:", RegexOptions.Multiline))
            text = Regex.Replace(text, @"^(\s*theme\s*:\s*).*$", $"$1{themeName}", RegexOptions.Multiline);
        else
            text = text.TrimEnd() + $"\n\ntheme: {themeName}\n";

        await File.WriteAllTextAsync(_context.ConfigPath, text).ConfigureAwait(false);
    }

    private async Task<CommandResult> RunHexoAsync(string args, CancellationToken ct)
    {
        // Always use the project's dependency, never a globally installed Hexo CLI.
        var result = await _runner.RunShellAsync($"npx --yes hexo {args}", _context.ProjectPath, ct).ConfigureAwait(false);
        return result;
    }

    private void EnsureProject()
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案資料夾。");
    }

    private static string? CleanVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Split('\n')[0].Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private static string? ExtractHexoVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Regex.Match(output, @"hexo:\s*([0-9.]+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(output, @"([0-9]+\.[0-9]+\.[0-9]+)");
        return m.Success ? m.Groups[1].Value : CleanVersion(output);
    }

    private static string Quote(string value)
    {
        if (value.Contains(' ', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal))
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return value;
    }
}
