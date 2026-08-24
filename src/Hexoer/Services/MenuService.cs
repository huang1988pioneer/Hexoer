using System.Text;
using System.Text.RegularExpressions;
using Hexoer.Models;

namespace Hexoer.Services;

/// <summary>
/// Reads and writes Hexo theme <c>menu:</c> / <c>social:</c> YAML (NexT, Butterfly, Fluid, Landscape).
/// Prefers a site-root <c>_config.&lt;theme&gt;.yml</c> override so the cloned theme stays clean.
/// </summary>
public sealed partial class MenuService
{
    public SiteMenuDocument Load(string sitePath)
    {
        var theme = ReadThemeName(sitePath);
        var themeConfig = FindThemeConfig(sitePath, theme);
        var overridePath = string.IsNullOrWhiteSpace(theme)
            ? null
            : Path.Combine(sitePath, $"_config.{theme}.yml");

        var entries = new List<MenuEntry>();
        var format = GuessDefaultFormat(theme);
        var savePath = overridePath ?? Path.Combine(sitePath, "_config.yml");

        if (!string.IsNullOrWhiteSpace(themeConfig) && File.Exists(themeConfig))
        {
            var parsed = ParseDocument(File.ReadAllText(themeConfig));
            if (parsed.Entries.Count > 0)
            {
                entries = parsed.Entries;
                format = parsed.Format;
                savePath = themeConfig;
            }
        }

        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            var parsed = ParseDocument(File.ReadAllText(overridePath));
            if (parsed.Entries.Count > 0)
            {
                entries = parsed.Entries;
                format = parsed.Format;
            }

            savePath = overridePath;
        }
        else if (!string.IsNullOrWhiteSpace(overridePath))
        {
            savePath = overridePath;
        }

        return new SiteMenuDocument
        {
            ConfigPath = savePath,
            MenuRootKey = "menu",
            Format = format,
            Entries = entries,
            FrontMatterFiles = [],
            ImportedFromFrontMatter = 0
        };
    }

    public void Save(string sitePath, SiteMenuDocument document, IReadOnlyList<MenuEntry> entries)
    {
        var path = string.IsNullOrWhiteSpace(document.ConfigPath)
            ? Path.Combine(sitePath, "_config.yml")
            : document.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var rendered = RenderMenus(entries, document.Format);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# Hexoer theme menu override\n" + rendered);
            return;
        }

        var original = File.ReadAllText(path);
        File.WriteAllText(path, ReplaceBlocks(original, rendered));
    }

    public static string UrlFromContentPath(string relativePath) =>
        ContentService.UrlFromContentPath(relativePath);

    private static (List<MenuEntry> Entries, string Format) ParseDocument(string text)
    {
        var entries = new List<MenuEntry>();
        var format = "pipe";
        foreach (var root in new[] { "menu", "social" })
        {
            var parsed = ParseRoot(text, root);
            if (parsed.Entries.Count == 0)
                continue;
            entries.AddRange(parsed.Entries);
            format = parsed.Format;
        }

        return (entries, format);
    }

    private static (List<MenuEntry> Entries, string Format) ParseRoot(string text, string rootKey)
    {
        var entries = new List<MenuEntry>();
        var format = "map";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var header = new Regex(@"^" + Regex.Escape(rootKey) + @":\s*$", RegexOptions.IgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            if (!header.IsMatch(lines[i]))
                continue;

            var headerIndent = IndentOf(lines[i]);
            var weight = 1;
            i++;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    i++;
                    continue;
                }

                var indent = IndentOf(line);
                if (indent <= headerIndent)
                    break;

                var trimmed = line.Trim();
                if (trimmed.StartsWith('-'))
                {
                    format = "list";
                    var item = ParseFlowMap(trimmed[1..].Trim().TrimStart('{').TrimEnd('}'));
                    var name = GetMap(item, "name");
                    entries.Add(new MenuEntry
                    {
                        MenuName = rootKey,
                        Name = name,
                        Identifier = string.IsNullOrWhiteSpace(GetMap(item, "identifier"))
                            ? Slugify(name)
                            : GetMap(item, "identifier"),
                        Url = GetMap(item, "url"),
                        Icon = GetMap(item, "icon"),
                        Weight = weight++
                    });
                    i++;
                    continue;
                }

                var (key, value) = SplitYaml(trimmed);
                if (string.IsNullOrWhiteSpace(value))
                {
                    format = "nested";
                    var entry = new MenuEntry
                    {
                        MenuName = rootKey,
                        Identifier = key,
                        Name = key,
                        Weight = weight++
                    };
                    i++;
                    while (i < lines.Length)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].TrimStart().StartsWith('#'))
                        {
                            i++;
                            continue;
                        }

                        if (IndentOf(lines[i]) <= indent)
                            break;

                        var (nestedKey, nestedValue) = SplitYaml(lines[i].Trim());
                        nestedValue = Unquote(nestedValue);
                        if (nestedKey.Equals("url", StringComparison.OrdinalIgnoreCase))
                            entry.Url = nestedValue;
                        else if (nestedKey.Equals("icon", StringComparison.OrdinalIgnoreCase))
                            entry.Icon = nestedValue;
                        else if (nestedKey.Equals("name", StringComparison.OrdinalIgnoreCase))
                            entry.Name = nestedValue;
                        i++;
                    }

                    entries.Add(entry);
                    continue;
                }

                var raw = Unquote(value);
                var url = raw;
                var icon = string.Empty;
                if (raw.Contains("||", StringComparison.Ordinal))
                {
                    format = "pipe";
                    var parts = raw.Split("||", 2, StringSplitOptions.TrimEntries);
                    url = parts[0];
                    icon = parts.Length > 1 ? parts[1] : string.Empty;
                }

                entries.Add(new MenuEntry
                {
                    MenuName = rootKey,
                    Identifier = key,
                    Name = key,
                    Url = url,
                    Icon = icon,
                    Weight = weight++
                });
                i++;
            }

            break;
        }

        return (entries, format);
    }

    private static string RenderMenus(IEnumerable<MenuEntry> entries, string format)
    {
        var groups = entries
            .Select(entry =>
            {
                var clone = entry.Clone();
                clone.MenuName = string.IsNullOrWhiteSpace(clone.MenuName) ? "menu" : clone.MenuName.Trim();
                return clone;
            })
            .GroupBy(entry => entry.MenuName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key.Equals("menu", StringComparison.OrdinalIgnoreCase) ? 0
                : group.Key.Equals("social", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        foreach (var group in groups)
        {
            if (builder.Length > 0)
                builder.AppendLine();
            builder.AppendLine($"{group.Key}:");
            foreach (var entry in group.OrderBy(item => item.Weight).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                AppendEntry(builder, entry, format);
        }

        return builder.ToString();
    }

    private static void AppendEntry(StringBuilder builder, MenuEntry entry, string format)
    {
        var key = string.IsNullOrWhiteSpace(entry.Identifier) ? Slugify(entry.Name) : entry.Identifier.Trim();
        var name = string.IsNullOrWhiteSpace(entry.Name) ? key : entry.Name.Trim();
        var url = string.IsNullOrWhiteSpace(entry.Url) ? "/" : entry.Url.Trim();
        var icon = entry.Icon.Trim();

        switch (format)
        {
            case "list":
                builder.Append("  - { name: ").Append(Quote(name))
                    .Append(", url: ").Append(Quote(url));
                if (!string.IsNullOrWhiteSpace(icon))
                    builder.Append(", icon: ").Append(Quote(icon));
                builder.AppendLine(" }");
                break;
            case "nested":
                builder.Append("  ").Append(key).AppendLine(":");
                builder.Append("    url: ").AppendLine(url);
                if (!string.IsNullOrWhiteSpace(icon))
                    builder.Append("    icon: ").AppendLine(icon);
                if (!string.Equals(name, key, StringComparison.Ordinal))
                    builder.Append("    name: ").AppendLine(Quote(name));
                break;
            case "map":
                builder.Append("  ").Append(key).Append(": ").AppendLine(url);
                break;
            default:
                var value = string.IsNullOrWhiteSpace(icon) ? url : $"{url} || {icon}";
                builder.Append("  ").Append(key).Append(": ").AppendLine(value);
                break;
        }
    }

    private static string ReplaceBlocks(string original, string rendered)
    {
        var text = original ?? string.Empty;
        var next = StripRoot(text, "menu");
        next = StripRoot(next, "social");
        next = next.TrimEnd() + "\n\n" + rendered.TrimEnd() + "\n";
        return next;
    }

    private static string StripRoot(string text, string rootKey)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var header = new Regex(@"^" + Regex.Escape(rootKey) + @":\s*$", RegexOptions.IgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!header.IsMatch(lines[i]))
                continue;

            var headerIndent = IndentOf(lines[i]);
            var end = i + 1;
            while (end < lines.Count)
            {
                var line = lines[end];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    end++;
                    continue;
                }

                if (IndentOf(line) <= headerIndent)
                    break;
                end++;
            }

            lines.RemoveRange(i, end - i);
            break;
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    private static string? ReadThemeName(string sitePath)
    {
        var config = Path.Combine(sitePath, "_config.yml");
        if (!File.Exists(config))
            return null;
        var match = Regex.Match(File.ReadAllText(config), @"^\s*theme\s*:\s*([^\s#]+)", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim('"', '\'') : null;
    }

    private static string? FindThemeConfig(string sitePath, string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
            return null;
        foreach (var name in new[] { "_config.yml", "_config.yaml" })
        {
            var path = Path.Combine(sitePath, "themes", theme, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string GuessDefaultFormat(string? theme) => theme?.ToLowerInvariant() switch
    {
        "next" or "butterfly" => "pipe",
        "fluid" => "list",
        "stellar" => "nested",
        _ => "map"
    };

    private static Dictionary<string, string> ParseFlowMap(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FlowMapRegex().Matches(text))
            map[match.Groups["key"].Value] = Unquote(match.Groups["value"].Value);
        return map;
    }

    private static string GetMap(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : string.Empty;

    private static (string Key, string Value) SplitYaml(string line)
    {
        var trimmed = line.Trim();
        var index = trimmed.IndexOf(':');
        if (index <= 0) return (trimmed, string.Empty);
        return (trimmed[..index].Trim(), trimmed[(index + 1)..].Trim());
    }

    private static int IndentOf(string line)
    {
        var count = 0;
        foreach (var character in line)
        {
            if (character == ' ') count++;
            else if (character == '\t') count += 2;
            else break;
        }

        return count;
    }

    private static string Unquote(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private static string Quote(string value) =>
        "'" + (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Slugify(string title)
    {
        var value = (title ?? string.Empty).Trim().ToLowerInvariant();
        value = Regex.Replace(value, @"\s+", "-");
        value = Regex.Replace(value, @"[^a-z0-9\u4e00-\u9fff\-_]", "");
        return string.IsNullOrWhiteSpace(value) ? "item" : value;
    }

    [GeneratedRegex(@"(?<key>[A-Za-z0-9_-]+)\s*:\s*(?<value>'[^']*'|""[^""]*""|[^,}\s]+)")]
    private static partial Regex FlowMapRegex();
}
