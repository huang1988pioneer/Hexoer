using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hexoer.Services;

public sealed class ConfigService
{
    private readonly ProjectContext _context;

    public ConfigService(ProjectContext context)
    {
        _context = context;
    }

    public bool ConfigExists => _context.IsHexoProject && File.Exists(_context.ConfigPath);

    public async Task<string> ReadSiteConfigAsync()
    {
        EnsureConfig();
        return await File.ReadAllTextAsync(_context.ConfigPath).ConfigureAwait(false);
    }

    public async Task SaveSiteConfigAsync(string content)
    {
        EnsureConfig();
        await File.WriteAllTextAsync(_context.ConfigPath, content).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> ReadSimpleKeysAsync(params string[] keys)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!ConfigExists) return map;

        var text = await ReadSiteConfigAsync().ConfigureAwait(false);
        foreach (var key in keys)
        {
            var match = Regex.Match(text, $@"^{Regex.Escape(key)}\s*:\s*(.+)$", RegexOptions.Multiline);
            if (match.Success)
                map[key] = match.Groups[1].Value.Trim().Trim('"', '\'');
        }

        return map;
    }

    public async Task UpsertSimpleKeyAsync(string key, string value)
    {
        EnsureConfig();
        var text = await ReadSiteConfigAsync().ConfigureAwait(false);
        var lineValue = NeedsQuotes(value) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

        if (Regex.IsMatch(text, $@"^{Regex.Escape(key)}\s*:", RegexOptions.Multiline))
        {
            text = Regex.Replace(
                text,
                $@"^({Regex.Escape(key)}\s*:\s*).*$",
                $"$1{lineValue}",
                RegexOptions.Multiline);
        }
        else
        {
            text = text.TrimEnd() + $"\n{key}: {lineValue}\n";
        }

        await SaveSiteConfigAsync(text).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates or inserts a deploy block for GitHub Pages using hexo-deployer-git.
    /// </summary>
    public async Task ConfigureGitDeployAsync(string repoUrl, string branch = "gh-pages")
    {
        EnsureConfig();
        var text = await ReadSiteConfigAsync().ConfigureAwait(false);

        var deployBlock =
            "deploy:\n" +
            "  type: git\n" +
            $"  repo: {repoUrl}\n" +
            $"  branch: {branch}\n";

        if (Regex.IsMatch(text, @"^deploy\s*:", RegexOptions.Multiline))
        {
            // Replace existing deploy section until next top-level key or EOF
            text = Regex.Replace(
                text,
                @"^deploy\s*:.*?(?=^\S|\z)",
                deployBlock + "\n",
                RegexOptions.Multiline | RegexOptions.Singleline);
        }
        else
        {
            text = text.TrimEnd() + "\n\n" + deployBlock;
        }

        await SaveSiteConfigAsync(text).ConfigureAwait(false);
    }

    public async Task UpdateSiteUrlAndRootAsync(string url, string root)
    {
        await UpsertSimpleKeyAsync("url", url).ConfigureAwait(false);
        await UpsertSimpleKeyAsync("root", root).ConfigureAwait(false);
    }

    public async Task<(string? Repo, string? Branch)> ReadDeploySettingsAsync()
    {
        if (!ConfigExists) return (null, null);
        var text = await ReadSiteConfigAsync().ConfigureAwait(false);

        string? repo = null;
        string? branch = null;
        var inDeploy = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Regex.IsMatch(line, @"^deploy\s*:"))
            {
                inDeploy = true;
                continue;
            }

            if (inDeploy)
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.TrimStart().StartsWith('#'))
                    break;

                var mRepo = Regex.Match(line, @"^\s*repo\s*:\s*(.+)$");
                if (mRepo.Success) repo = mRepo.Groups[1].Value.Trim().Trim('"', '\'');

                var mBranch = Regex.Match(line, @"^\s*branch\s*:\s*(.+)$");
                if (mBranch.Success) branch = mBranch.Groups[1].Value.Trim().Trim('"', '\'');
            }
        }

        return (repo, branch);
    }

    private void EnsureConfig()
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案。");
        if (!File.Exists(_context.ConfigPath))
            throw new InvalidOperationException("_config.yml 不存在。");
    }

    private static bool NeedsQuotes(string value) =>
        value.Contains(':') || value.Contains('#') || value.Contains(' ') || value.Length == 0;
}
