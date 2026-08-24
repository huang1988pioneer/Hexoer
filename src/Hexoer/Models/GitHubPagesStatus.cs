using System;

namespace Hexoer.Models;

public sealed class GitHubPagesStatus
{
    public bool Success { get; set; }
    public bool Enabled { get; set; }
    public string? Status { get; set; }
    public string? HtmlUrl { get; set; }
    public string? Cname { get; set; }
    public string? SourceBranch { get; set; }
    public string? SourcePath { get; set; }
    public string? BuildType { get; set; }
    public string? Message { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class GitRemoteInfo
{
    public string? RemoteUrl { get; set; }
    public RemoteGitProvider Provider { get; set; } = RemoteGitProvider.Unknown;
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public bool GhAuthenticated { get; set; }
    public string? GhUser { get; set; }
}

public sealed class GitHubRepositoryTarget
{
    public bool IsValid { get; init; }
    public RemoteGitProvider Provider { get; init; } = RemoteGitProvider.Unknown;
    public string ProviderName => Provider switch
    {
        RemoteGitProvider.GitHub => "GitHub",
        RemoteGitProvider.GitLab => "GitLab",
        RemoteGitProvider.Codeberg => "Codeberg",
        RemoteGitProvider.Bitbucket => "Bitbucket",
        _ => "Git"
    };
    public string? Owner { get; init; }
    public string? Repository { get; init; }
    public string? CanonicalUrl { get; init; }
    public string? PagesUrl { get; init; }
    public bool IsUserOrOrganizationSite { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public enum RemoteGitProvider
{
    Unknown,
    GitHub,
    GitLab,
    Codeberg,
    Bitbucket
}

public sealed class RemoteHexoSiteInfo
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public GitHubRepositoryTarget Target { get; init; } = new();
    public bool RepositoryExists { get; init; }
    public bool PagesEnabled { get; init; }
    public string? PagesUrl { get; init; }
    public string? PagesStatus { get; init; }
    public string? DefaultBranch { get; init; }
    public string? HexoSourceBranch { get; init; }
    public bool LooksLikeHexoSource { get; init; }
    public bool LooksLikeGeneratedSite { get; init; }
    public bool IsPrivate { get; init; }
    public bool UsedGitHubCli { get; init; }
}

public sealed class CloneSiteResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? LocalPath { get; init; }
    public string? Branch { get; init; }
    public bool LooksLikeHexoSource { get; init; }
    public bool InstalledDependencies { get; init; }
    public bool ReusedExisting { get; init; }
}

public enum DeploymentVersionState
{
    NotConfigured,
    Previous,
    Latest,
    Unavailable
}

public sealed class DeploymentMarker
{
    public string DeploymentId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class DeploymentCheckResult
{
    public DeploymentVersionState State { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ExpectedDeploymentId { get; init; }
    public string? LiveDeploymentId { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
}
