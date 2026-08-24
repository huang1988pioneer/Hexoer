namespace Hexoer.Models;

public sealed class EnvironmentStatus
{
    public bool NodeInstalled { get; set; }
    public string? NodeVersion { get; set; }
    public bool NpmInstalled { get; set; }
    public string? NpmVersion { get; set; }
    public bool GitInstalled { get; set; }
    public string? GitVersion { get; set; }
    public bool HexoCliInstalled { get; set; }
    public string? HexoVersion { get; set; }
    public string? LatestHexoVersion { get; set; }
    public bool? HexoIsLatest { get; set; }
    public string? HexoVersionNotice { get; set; }
    public bool ProjectValid { get; set; }
    public string? ProjectPath { get; set; }
    public string? ThemeName { get; set; }
}
