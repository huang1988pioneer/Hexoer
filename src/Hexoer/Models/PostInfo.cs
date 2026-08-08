using System;

namespace Hexoer.Models;

public sealed class PostInfo
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Title { get; set; }
    public DateTime LastModified { get; init; }
    public bool IsDraft { get; init; }
}
