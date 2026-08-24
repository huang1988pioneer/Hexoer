using System.Collections.Generic;

namespace Hexoer.Models;

public enum MenuEntrySource
{
    Config,
    FrontMatter
}

public sealed class MenuEntry
{
    public string MenuName { get; set; } = "menu";
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string PageRef { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public int Weight { get; set; }
    public string Icon { get; set; } = string.Empty;
    public bool NewTab { get; set; }
    public MenuEntrySource Source { get; set; } = MenuEntrySource.Config;
    public string? SourcePath { get; set; }

    public MenuEntry Clone() => new()
    {
        MenuName = MenuName,
        Identifier = Identifier,
        Name = Name,
        Url = Url,
        PageRef = PageRef,
        Parent = Parent,
        Weight = Weight,
        Icon = Icon,
        NewTab = NewTab,
        Source = Source,
        SourcePath = SourcePath
    };
}

public sealed class SiteMenuDocument
{
    public string ConfigPath { get; init; } = string.Empty;
    public bool IsDedicatedMenuFile { get; init; }
    public string MenuRootKey { get; init; } = "menu";
    /// <summary>YAML shape: pipe (NexT/Butterfly), map, nested, or list (Fluid).</summary>
    public string Format { get; init; } = "pipe";
    public IReadOnlyList<MenuEntry> Entries { get; init; } = [];
    public IReadOnlyList<string> FrontMatterFiles { get; init; } = [];
    public int ImportedFromFrontMatter { get; init; }
}
