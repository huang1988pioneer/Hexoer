using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Hexoer.Services;

public sealed class MarkdownPreviewService
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    /// <summary>
    /// Convert Hexo-style Markdown (with optional YAML front matter) to a full HTML document for preview.
    /// </summary>
    public string ToPreviewHtml(string? markdown)
    {
        var source = markdown ?? string.Empty;
        var bodyMd = StripFrontMatter(source);
        string bodyHtml;
        try
        {
            bodyHtml = Markdown.ToHtml(bodyMd, _pipeline);
        }
        catch (Exception ex)
        {
            bodyHtml = $"<pre class=\"error\">預覽轉換失敗：{WebUtility.HtmlEncode(ex.Message)}</pre>";
        }

        var sb = new StringBuilder(bodyHtml.Length + 1200);
        sb.Append("""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8" />
            <style>
              body {
                font-family: "Segoe UI", "Microsoft JhengHei", "Noto Sans CJK", sans-serif;
                font-size: 15px;
                line-height: 1.65;
                color: #e8eaed;
                background: transparent;
                margin: 0;
                padding: 12px 16px 24px;
              }
              h1, h2, h3, h4, h5, h6 {
                line-height: 1.3;
                margin: 1.2em 0 0.5em;
                font-weight: 600;
              }
              h1 { font-size: 1.75em; border-bottom: 1px solid #334155; padding-bottom: 0.3em; }
              h2 { font-size: 1.4em; border-bottom: 1px solid #1e293b; padding-bottom: 0.25em; }
              h3 { font-size: 1.2em; }
              p, ul, ol, blockquote, pre, table { margin: 0.75em 0; }
              a { color: #60a5fa; text-decoration: none; }
              a:hover { text-decoration: underline; }
              code {
                font-family: Consolas, "Cascadia Mono", monospace;
                font-size: 0.9em;
                background: #1e293b;
                padding: 0.15em 0.4em;
                border-radius: 4px;
              }
              pre {
                background: #0f172a;
                border: 1px solid #1e293b;
                border-radius: 8px;
                padding: 12px 14px;
                overflow-x: auto;
              }
              pre code { background: transparent; padding: 0; }
              blockquote {
                border-left: 4px solid #3b82f6;
                margin-left: 0;
                padding: 0.25em 0 0.25em 1em;
                color: #cbd5e1;
                background: #0f172a55;
              }
              table { border-collapse: collapse; width: 100%; }
              th, td { border: 1px solid #334155; padding: 6px 10px; }
              th { background: #1e293b; }
              img { max-width: 100%; height: auto; border-radius: 6px; }
              hr { border: none; border-top: 1px solid #334155; margin: 1.5em 0; }
              .front-matter {
                font-size: 12px;
                color: #94a3b8;
                background: #0f172a;
                border: 1px dashed #334155;
                border-radius: 8px;
                padding: 8px 12px;
                margin-bottom: 16px;
                white-space: pre-wrap;
                font-family: Consolas, monospace;
              }
              .error { color: #fca5a5; }
            </style>
            </head>
            <body>
            """);

        var fm = ExtractFrontMatter(source);
        if (!string.IsNullOrWhiteSpace(fm))
        {
            sb.Append("<div class=\"front-matter\">");
            sb.Append(WebUtility.HtmlEncode(fm.Trim()));
            sb.Append("</div>");
        }

        sb.Append(bodyHtml);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string StripFrontMatter(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        // Hexo / Jekyll YAML front matter between --- lines
        var m = Regex.Match(markdown, @"\A---\s*\r?\n.*?\r?\n---\s*\r?\n?", RegexOptions.Singleline);
        if (m.Success)
            return markdown[m.Length..];

        return markdown;
    }

    public static string? ExtractFrontMatter(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return null;

        var m = Regex.Match(markdown, @"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n?", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }
}
