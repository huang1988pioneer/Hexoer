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
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public bool GhAuthenticated { get; set; }
    public string? GhUser { get; set; }
}

public sealed class GitHubRepositoryTarget
{
    public bool IsValid { get; init; }
    public string? Owner { get; init; }
    public string? Repository { get; init; }
    public string? CanonicalUrl { get; init; }
    public string? PagesUrl { get; init; }
    public bool IsUserOrOrganizationSite { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
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
