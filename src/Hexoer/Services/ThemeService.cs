using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

public sealed class ThemeService
{
    private readonly ProcessRunner _runner;
    private readonly ProjectContext _context;
    private readonly HexoService _hexo;

    public ThemeService(ProcessRunner runner, ProjectContext context, HexoService hexo)
    {
        _runner = runner;
        _context = context;
        _hexo = hexo;
    }

    public static IReadOnlyList<ThemeInfo> Catalog { get; } =
    [
        new ThemeInfo
        {
            Name = "next",
            DisplayName = "NexT",
            Description = "Elegant and powerful theme with multiple schemes (Muse / Mist / Pisces / Gemini).",
            GitUrl = "https://github.com/next-theme/hexo-theme-next.git",
            ConfigFileName = "_config.yml"
        },
        new ThemeInfo
        {
            Name = "butterfly",
            DisplayName = "Butterfly",
            Description = "Card-style theme with rich features and beautiful animations.",
            GitUrl = "https://github.com/jerryc127/hexo-theme-butterfly.git",
            ConfigFileName = "_config.yml"
        },
        new ThemeInfo
        {
            Name = "fluid",
            DisplayName = "Fluid",
            Description = "Material Design inspired elegant blog theme.",
            GitUrl = "https://github.com/fluid-dev/hexo-theme-fluid.git",
            ConfigFileName = "_config.yml"
        },
        new ThemeInfo
        {
            Name = "landscape",
            DisplayName = "Landscape",
            Description = "Official default Hexo theme (simple and lightweight).",
            GitUrl = "https://github.com/hexojs/hexo-theme-landscape.git",
            ConfigFileName = "_config.yml"
        },
        new ThemeInfo
        {
            Name = "stellar",
            DisplayName = "Stellar",
            Description = "Modern multi-functional theme with wiki / notebook support.",
            GitUrl = "https://github.com/xaoxuu/hexo-theme-stellar.git",
            ConfigFileName = "_config.yml"
        }
    ];

    public IReadOnlyList<ThemeInfo> GetThemesWithInstallState()
    {
        var themesDir = _context.ThemesDir;
        return Catalog.Select(t =>
        {
            var path = Path.Combine(themesDir, t.Name);
            var installed = Directory.Exists(path) && File.Exists(Path.Combine(path, "_config.yml"));
            return new ThemeInfo
            {
                Name = t.Name,
                DisplayName = t.DisplayName,
                Description = t.Description,
                GitUrl = t.GitUrl,
                ConfigFileName = t.ConfigFileName,
                IsInstalled = installed,
                LocalPath = installed ? path : null
            };
        }).ToList();
    }

    public IEnumerable<string> GetInstalledThemeNames()
    {
        if (!Directory.Exists(_context.ThemesDir))
            yield break;

        foreach (var dir in Directory.GetDirectories(_context.ThemesDir))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
        }
    }

    public async Task<CommandResult> InstallThemeAsync(ThemeInfo theme, CancellationToken ct = default)
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案。");

        Directory.CreateDirectory(_context.ThemesDir);
        var target = Path.Combine(_context.ThemesDir, theme.Name);

        if (Directory.Exists(target))
        {
            return new CommandResult
            {
                ExitCode = 0,
                StandardOutput = $"主題 {theme.Name} 已存在於 {target}",
                StandardError = string.Empty
            };
        }

        var result = await _runner.RunShellAsync(
            $"git clone --depth 1 {Quote(theme.GitUrl)} {Quote(target)}",
            _context.ProjectPath,
            ct).ConfigureAwait(false);

        return result;
    }

    public async Task ActivateThemeAsync(string themeName, CancellationToken ct = default)
    {
        await _hexo.SetActiveThemeAsync(themeName).ConfigureAwait(false);

        // NexT recommends installing optional packages sometimes; keep activation simple.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public string? GetThemeConfigPath(string themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName) || !_context.HasProject)
            return null;

        var candidates = new[]
        {
            Path.Combine(_context.ThemesDir, themeName, "_config.yml"),
            Path.Combine(_context.ThemesDir, themeName, "_config.yaml"),
            // Hexo also supports theme config override in site root
            Path.Combine(_context.ProjectPath!, $"_config.{themeName}.yml")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string?> ReadThemeConfigAsync(string themeName)
    {
        var path = GetThemeConfigPath(themeName);
        if (path is null || !File.Exists(path))
            return null;
        return await File.ReadAllTextAsync(path).ConfigureAwait(false);
    }

    public async Task SaveThemeConfigAsync(string themeName, string content)
    {
        var path = GetThemeConfigPath(themeName)
                   ?? Path.Combine(_context.ThemesDir, themeName, "_config.yml");
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
