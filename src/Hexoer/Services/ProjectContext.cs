using System;
using System.IO;
using Hexoer.Models;

namespace Hexoer.Services;

/// <summary>
/// Shared project path and app settings for all feature services/view models.
/// </summary>
public sealed class ProjectContext
{
    private string? _projectPath;

    public AppSettings Settings { get; } = AppSettings.Load();

    public string? ProjectPath
    {
        get => _projectPath;
        set
        {
            if (_projectPath == value) return;
            _projectPath = value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                Settings.LastProjectPath = value;
                Settings.Save();
            }
            ProjectChanged?.Invoke(value);
        }
    }

    public event Action<string?>? ProjectChanged;

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectPath) && Directory.Exists(ProjectPath);

    public bool IsHexoProject
    {
        get
        {
            if (!HasProject) return false;
            return File.Exists(Path.Combine(ProjectPath!, "_config.yml"))
                   || File.Exists(Path.Combine(ProjectPath!, "package.json"));
        }
    }

    public string ConfigPath => Path.Combine(ProjectPath ?? string.Empty, "_config.yml");
    public string PackageJsonPath => Path.Combine(ProjectPath ?? string.Empty, "package.json");
    public string ThemesDir => Path.Combine(ProjectPath ?? string.Empty, "themes");
    public string SourceDir => Path.Combine(ProjectPath ?? string.Empty, "source");
    public string PostsDir => Path.Combine(SourceDir, "_posts");
    public string DraftsDir => Path.Combine(SourceDir, "_drafts");

    public void RestoreLastProject()
    {
        if (!string.IsNullOrWhiteSpace(Settings.LastProjectPath) && Directory.Exists(Settings.LastProjectPath))
            ProjectPath = Settings.LastProjectPath;
    }
}
