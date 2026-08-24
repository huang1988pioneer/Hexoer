using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hexoer.Models;

public sealed class AppSettings
{
    public string? LastProjectPath { get; set; }
    public string? DefaultAuthor { get; set; }
    /// <summary>Last Markdown editor mode: Wysiwyg or Source.</summary>
    public string MarkdownEditorMode { get; set; } = "Wysiwyg";
    public string SelectedDeployProvider { get; set; } = nameof(RemoteGitProvider.GitHub);
    public string SelectedSetupProvider { get; set; } = nameof(RemoteGitProvider.GitHub);
    public Dictionary<string, RemoteProviderSettings> RemoteProviders { get; set; } = new();

    public void SetMarkdownEditorMode(string mode)
    {
        MarkdownEditorMode = string.IsNullOrWhiteSpace(mode) ? "Wysiwyg" : mode;
        Save();
    }

    public RemoteGitProvider GetSelectedDeployProvider() => ParseProvider(SelectedDeployProvider);

    public RemoteGitProvider GetSelectedSetupProvider() => ParseProvider(SelectedSetupProvider);

    public void SetSelectedDeployProvider(RemoteGitProvider provider)
    {
        SelectedDeployProvider = ProviderKey(provider);
        Save();
    }

    public void SetSelectedSetupProvider(RemoteGitProvider provider)
    {
        SelectedSetupProvider = ProviderKey(provider);
        Save();
    }

    public RemoteProviderSettings GetRemoteProviderSettings(RemoteGitProvider provider)
    {
        var key = ProviderKey(provider);
        if (!RemoteProviders.TryGetValue(key, out var settings))
        {
            settings = new RemoteProviderSettings();
            RemoteProviders[key] = settings;
        }

        return settings;
    }

    private static RemoteGitProvider ParseProvider(string? value) =>
        Enum.TryParse<RemoteGitProvider>(value, ignoreCase: true, out var provider)
            && provider is RemoteGitProvider.GitHub
                or RemoteGitProvider.GitLab
                or RemoteGitProvider.Codeberg
                or RemoteGitProvider.Bitbucket
            ? provider
            : RemoteGitProvider.GitHub;

    private static string ProviderKey(RemoteGitProvider provider) =>
        ParseProvider(provider.ToString()).ToString();

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hexoer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // ignore corrupt settings
        }

        return new AppSettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}

public sealed class RemoteProviderSettings
{
    public string RepositoryUrl { get; set; } = string.Empty;
    public string CloneRepositoryUrl { get; set; } = string.Empty;
    public string DeployerRepoUrl { get; set; } = string.Empty;
    public string DeployerBranch { get; set; } = "gh-pages";
}
