using System;

namespace Hexoer.Models;

public enum MarkdownEditorMode
{
    Wysiwyg,
    Source
}

/// <summary>
/// CKEditor 5-style corresponding preview: rendered HTML, or the Markdown source output.
/// </summary>
public enum MarkdownPreviewKind
{
    Render,
    MarkdownOutput
}

public sealed class ContentItem
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public DateTime LastWriteTime { get; init; }
    public string LastWriteTimeText => LastWriteTime.ToString("yyyy/MM/dd HH:mm");
    public DateTimeOffset? ArticleDate { get; init; }
    public string ArticleDateText => ArticleDate?.ToString("yyyy/MM/dd") ?? "未設定日期";
    public string ArticleTitle { get; init; } = string.Empty;
    public string DisplayTitle => string.IsNullOrWhiteSpace(ArticleTitle) ? Name : ArticleTitle;
    public bool IsDraft { get; init; }
    public bool IsPublished => !IsDraft;
    public bool HasArticleDate => ArticleDate.HasValue;
    public string PublicationStatusText => IsDraft ? "草稿" : "已發布";
    public string TimelineText => ArticleDate.HasValue
        ? $"文章 {ArticleDate:yyyy/MM/dd} · 更新 {LastWriteTime:yyyy/MM/dd HH:mm}"
        : $"未設定日期 · 更新 {LastWriteTime:yyyy/MM/dd HH:mm}";
}
