using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hexoer.Models;

namespace Hexoer.Services;

public sealed class ContentService
{
    private static readonly string[] ArticleFolders = ["_posts", "_drafts"];
    private static readonly string[] SkippedSourceFolders = ["_posts", "_drafts", "_data"];

    private readonly ProjectContext _context;
    private readonly HexoService _hexo;
    private readonly FrontMatterService _frontMatter;

    public ContentService(ProjectContext context, HexoService hexo, FrontMatterService frontMatter)
    {
        _context = context;
        _hexo = hexo;
        _frontMatter = frontMatter;
    }

    public IReadOnlyList<PostInfo> ListPosts(bool includeDrafts = true)
    {
        return ListArticles()
            .Where(item => includeDrafts || !item.IsDraft)
            .Select(item => new PostInfo
            {
                FilePath = item.FullPath,
                FileName = item.Name,
                Title = item.DisplayTitle,
                LastModified = item.LastWriteTime,
                IsDraft = item.IsDraft
            })
            .ToList();
    }

    public IReadOnlyList<ContentItem> ListArticles()
    {
        if (!_context.IsHexoProject) return [];

        var items = new List<ContentItem>();
        AddMarkdownFrom(_context.PostsDir, "source/_posts", draftFolder: false, items);
        AddMarkdownFrom(_context.DraftsDir, "source/_drafts", draftFolder: true, items);
        return items;
    }

    public IReadOnlyList<ContentItem> ListSitePages()
    {
        if (!_context.IsHexoProject || !Directory.Exists(_context.SourceDir))
            return [];

        var items = new List<ContentItem>();
        foreach (var file in Directory.EnumerateFiles(_context.SourceDir, "*.*", SearchOption.AllDirectories)
                     .Where(IsMarkdown))
        {
            var relative = Path.GetRelativePath(_context.SourceDir, file).Replace('\\', '/');
            var first = relative.Split('/')[0];
            if (SkippedSourceFolders.Contains(first, StringComparer.OrdinalIgnoreCase))
                continue;

            items.Add(ToContentItem(file, "source/" + relative, draftFolder: false));
        }

        return items
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<string> ReadAsync(string filePath) => File.ReadAllTextAsync(filePath);

    public async Task<string> ReadPostAsync(string filePath) => await ReadAsync(filePath).ConfigureAwait(false);

    public async Task SaveAsync(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(filePath, content).ConfigureAwait(false);
    }

    public Task SavePostAsync(string filePath, string content) => SaveAsync(filePath, content);

    public async Task<string> CreatePostAsync(string title, bool asDraft = false)
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案。");

        var result = await _hexo.NewPostAsync(title, asDraft).ConfigureAwait(false);
        if (!result.Success)
        {
            var slug = Slugify(title);
            var dir = asDraft ? _context.DraftsDir : _context.PostsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}-{slug}.md");
            await SaveAsync(path, DefaultFrontMatter(title)).ConfigureAwait(false);
            return path;
        }

        var posts = ListArticles();
        var match = posts.FirstOrDefault(p =>
            p.ArticleTitle.Equals(title, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains(Slugify(title), StringComparison.OrdinalIgnoreCase));

        return match?.FullPath
               ?? posts.OrderByDescending(p => p.LastWriteTime).FirstOrDefault()?.FullPath
               ?? throw new InvalidOperationException("文章已建立，但找不到檔案路徑。");
    }

    public async Task<string> CreatePageAsync(string title, string folder)
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案。");

        var slug = Slugify(folder);
        var dir = Path.Combine(_context.SourceDir, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "index.md");
        if (File.Exists(path))
            throw new InvalidOperationException($"頁面已存在：source/{slug}/index.md");

        await SaveAsync(path, DefaultFrontMatter(title, layout: "page")).ConfigureAwait(false);
        return path;
    }

    public void Delete(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    public void DeletePost(string filePath) => Delete(filePath);

    public static string UrlFromContentPath(string relativePath)
    {
        var relative = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (relative.StartsWith("source/", StringComparison.OrdinalIgnoreCase))
            relative = relative["source/".Length..];

        if (relative.EndsWith("/index.md", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith("/index.markdown", StringComparison.OrdinalIgnoreCase))
            relative = relative[..relative.LastIndexOf('/')];
        else if (relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                 || relative.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
            relative = relative[..relative.LastIndexOf('.')];

        if (string.IsNullOrWhiteSpace(relative) || relative == "index")
            return "/";
        return "/" + relative.Trim('/') + "/";
    }

    private void AddMarkdownFrom(string dir, string relativePrefix, bool draftFolder, List<ContentItem> items)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(IsMarkdown))
        {
            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            items.Add(ToContentItem(file, $"{relativePrefix}/{relative}", draftFolder));
        }
    }

    private ContentItem ToContentItem(string file, string relativePath, bool draftFolder)
    {
        var metadata = ReadArticleMetadata(file);
        return new ContentItem
        {
            FullPath = file,
            RelativePath = relativePath.Replace('\\', '/'),
            Name = Path.GetFileName(file),
            LastWriteTime = File.GetLastWriteTime(file),
            ArticleDate = metadata.Date,
            ArticleTitle = metadata.Title,
            IsDraft = draftFolder || metadata.IsDraft
        };
    }

    private (string Title, DateTimeOffset? Date, bool IsDraft) ReadArticleMetadata(string file)
    {
        try
        {
            var document = _frontMatter.Parse(File.ReadAllText(file));
            document.Fields.TryGetValue("title", out var title);
            DateTimeOffset? date = null;
            if (document.Fields.TryGetValue("date", out var dateText)
                && DateTimeOffset.TryParse(dateText, out var parsed))
                date = parsed;

            var isDraft = false;
            if (document.Fields.TryGetValue("published", out var published)
                && published.Equals("false", StringComparison.OrdinalIgnoreCase))
                isDraft = true;
            if (document.Fields.TryGetValue("draft", out var draft)
                && draft.Equals("true", StringComparison.OrdinalIgnoreCase))
                isDraft = true;

            return (title ?? Path.GetFileNameWithoutExtension(file), date, isDraft);
        }
        catch
        {
            return (Path.GetFileNameWithoutExtension(file), null, false);
        }
    }

    private static string DefaultFrontMatter(string title, string? layout = null)
    {
        var extra = string.IsNullOrWhiteSpace(layout) ? string.Empty : $"layout: {layout}\n";
        return
            "---\n" +
            $"title: {title}\n" +
            $"date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
            extra +
            "tags:\n" +
            "---\n\n";
    }

    private static bool IsMarkdown(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static string Slugify(string title)
    {
        var s = title.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"\s+", "-");
        s = Regex.Replace(s, @"[^a-z0-9\u4e00-\u9fff\-]+", "");
        s = Regex.Replace(s, @"-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(s) ? "post" : s;
    }
}
