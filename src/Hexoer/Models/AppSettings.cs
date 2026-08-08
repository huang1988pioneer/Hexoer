using System;
using System.IO;
using System.Text.Json;

namespace Hexoer.Models;

public sealed class AppSettings
{
    public string? LastProjectPath { get; set; }
    public string? GitHubToken { get; set; }
    public string? DefaultAuthor { get; set; }

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
