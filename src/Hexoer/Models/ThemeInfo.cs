namespace Hexoer.Models;

public sealed class ThemeInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string GitUrl { get; init; }
    public string? ConfigFileName { get; init; }
    public bool IsInstalled { get; set; }
    public string? LocalPath { get; set; }
}
