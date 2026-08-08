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
    private readonly ProjectContext _context;
    private readonly HexoService _hexo;

    public ContentService(ProjectContext context, HexoService hexo)
    {
        _context = context;
        _hexo = hexo;
    }

    public IReadOnlyList<PostInfo> ListPosts(bool includeDrafts = true)
    {
        var list = new List<PostInfo>();
        if (!_context.IsHexoProject) return list;

        void AddFrom(string dir, bool draft)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(dir, "*.markdown", SearchOption.AllDirectories)))
            {
                var title = ExtractTitle(file) ?? Path.GetFileNameWithoutExtension(file);
                list.Add(new PostInfo
                {
                    FilePath = file,
                    FileName = Path.GetFileName(file),
                    Title = title,
                    LastModified = File.GetLastWriteTime(file),
                    IsDraft = draft
                });
            }
        }

        AddFrom(_context.PostsDir, false);
        if (includeDrafts)
            AddFrom(_context.DraftsDir, true);

        return list.OrderByDescending(p => p.LastModified).ToList();
    }

    public async Task<string> ReadPostAsync(string filePath)
    {
        return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
    }

    public async Task SavePostAsync(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(filePath, content).ConfigureAwait(false);
    }

    public async Task<string> CreatePostAsync(string title, bool asDraft = false)
    {
        if (!_context.IsHexoProject)
            throw new InvalidOperationException("尚未選擇有效的 Hexo 專案。");

        var result = await _hexo.NewPostAsync(title, asDraft).ConfigureAwait(false);
        if (!result.Success)
        {
            // Fallback: create file manually
            var slug = Slugify(title);
            var dir = asDraft ? _context.DraftsDir : _context.PostsDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}-{slug}.md");
            var frontMatter =
                "---\n" +
                $"title: {title}\n" +
                $"date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                "tags:\n" +
                "---\n\n";
            await File.WriteAllTextAsync(path, frontMatter).ConfigureAwait(false);
            return path;
        }

        // Try to find newly created file
        var posts = ListPosts(includeDrafts: true);
        var match = posts.FirstOrDefault(p =>
            p.Title.Equals(title, StringComparison.OrdinalIgnoreCase) ||
            p.FileName.Contains(Slugify(title), StringComparison.OrdinalIgnoreCase));

        return match?.FilePath
               ?? posts.OrderByDescending(p => p.LastModified).FirstOrDefault()?.FilePath
               ?? throw new InvalidOperationException("文章已建立，但找不到檔案路徑。");
    }

    public void DeletePost(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    private static string? ExtractTitle(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            var first = reader.ReadLine();
            if (first is null || !first.Trim().StartsWith("---", StringComparison.Ordinal))
                return null;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim().StartsWith("---", StringComparison.Ordinal))
                    break;
                var m = Regex.Match(line, @"^title\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                    return m.Groups[1].Value.Trim().Trim('"', '\'');
            }
        }
        catch
        {
            // ignore
        }

        return null;
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
