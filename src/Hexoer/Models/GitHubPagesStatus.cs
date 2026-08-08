using System;

namespace Hexoer.Models;

public sealed class GitHubPagesStatus
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? HtmlUrl { get; set; }
    public string? Cname { get; set; }
    public string? SourceBranch { get; set; }
    public string? SourcePath { get; set; }
    public string? Message { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
