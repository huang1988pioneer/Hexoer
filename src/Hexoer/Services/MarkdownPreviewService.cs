using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Hexoer.Services;

public sealed class MarkdownPreviewService
{
    private readonly MarkdownPipeline _pipeline = SharedPipeline;

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

    public static string ToHtmlFragment(string markdown)
    {
        var bodyMd = StripFrontMatter(markdown);
        if (string.IsNullOrWhiteSpace(bodyMd))
            return string.Empty;
        return Markdown.ToHtml(bodyMd, SharedPipeline).Trim();
    }

    public static string ToHtmlDocument(string markdown, string? title = null)
    {
        var preview = new MarkdownPreviewService();
        var html = preview.ToPreviewHtml(markdown);
        if (string.IsNullOrWhiteSpace(title))
            return html;
        return html.Replace("<head>", $"<head><title>{WebUtility.HtmlEncode(title)}</title>", StringComparison.Ordinal);
    }

    private static readonly MarkdownPipeline SharedPipeline = CreatePipeline();

    private static MarkdownPipeline CreatePipeline() => new MarkdownPipelineBuilder()
        // Advanced extensions cover tables, grid tables, task lists, footnotes,
        // definition lists, figures, citations, custom containers, and attributes.
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UseEmojiAndSmiley(true)
        .UseSmartyPants()
        .UseMediaLinks()
        .UseMathematics()
        .UseDiagrams()
        .UseGenericAttributes()
        .UseCjkFriendlyEmphasis()
        .UseGlobalization()
        .Build();

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

    /// <summary>
    /// Empty WebView shell for live preview. Call <c>hugoerSetPreview(html)</c> to replace the article.
    /// </summary>
    public static string PreviewShellDocument()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-Hant\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data: https: http: file: blob:; media-src data: https: http: file: blob:;\"/>");
        sb.AppendLine("<title>Preview</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(DarkPreviewCss);
        sb.AppendLine("#placeholder { display:block; color:#6b7785; font-style:italic; padding:20px 24px; }");
        sb.AppendLine("#content { display:none; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<p id=\"placeholder\">開始輸入 Markdown，預覽會即時更新。</p>");
        sb.AppendLine("<article id=\"content\" class=\"markdown-body\"></article>");
        sb.AppendLine("<script>");
        sb.AppendLine("window.hugoerMedia = {};");
        sb.AppendLine("function hugoerLookupMedia(src) {");
        sb.AppendLine("  if (!src) return null;");
        sb.AppendLine("  if (window.hugoerMedia[src]) return window.hugoerMedia[src];");
        sb.AppendLine("  try {");
        sb.AppendLine("    var decoded = decodeURI(src);");
        sb.AppendLine("    if (decoded !== src && window.hugoerMedia[decoded]) return window.hugoerMedia[decoded];");
        sb.AppendLine("  } catch (e) {}");
        sb.AppendLine("  return null;");
        sb.AppendLine("}");
        sb.AppendLine("function hugoerApplyMedia(root) {");
        sb.AppendLine("  if (!root) return;");
        sb.AppendLine("  root.querySelectorAll('img[src], audio[src], video[src], source[src]').forEach(function (el) {");
        sb.AppendLine("    var src = el.getAttribute('src');");
        sb.AppendLine("    if (!src || /^(data:|blob:|https?:|file:)/i.test(src)) return;");
        sb.AppendLine("    var mapped = hugoerLookupMedia(src);");
        sb.AppendLine("    if (!mapped) return;");
        sb.AppendLine("    el.setAttribute('src', mapped);");
        sb.AppendLine("  });");
        sb.AppendLine("}");
        sb.AppendLine("window.hugoerSetPreview = function (html, media) {");
        sb.AppendLine("  if (media) {");
        sb.AppendLine("    var keys = Object.keys(media);");
        sb.AppendLine("    for (var i = 0; i < keys.length; i++) window.hugoerMedia[keys[i]] = media[keys[i]];");
        sb.AppendLine("  }");
        sb.AppendLine("  var content = document.getElementById('content');");
        sb.AppendLine("  var placeholder = document.getElementById('placeholder');");
        sb.AppendLine("  var scroller = document.scrollingElement || document.documentElement;");
        sb.AppendLine("  var top = scroller ? scroller.scrollTop : 0;");
        sb.AppendLine("  var empty = !html;");
        sb.AppendLine("  placeholder.style.display = empty ? 'block' : 'none';");
        sb.AppendLine("  content.style.display = empty ? 'none' : 'block';");
        sb.AppendLine("  var wrap = document.createElement('div');");
        sb.AppendLine("  wrap.innerHTML = html || '';");
        sb.AppendLine("  hugoerApplyMedia(wrap);");
        sb.AppendLine("  content.innerHTML = wrap.innerHTML;");
        sb.AppendLine("  if (scroller) scroller.scrollTop = top;");
        sb.AppendLine("};");
        sb.AppendLine("document.addEventListener('click', function (event) {");
        sb.AppendLine("  var link = event.target && event.target.closest ? event.target.closest('a[href]') : null;");
        sb.AppendLine("  if (link) event.preventDefault();");
        sb.AppendLine("});");
        sb.AppendLine("</script></body></html>");
        return sb.ToString();
    }

    private const string DarkPreviewCss = """
:root { color-scheme: dark; }
html, body {
  margin: 0; padding: 0;
  background: #0d1218;
  color: #e6edf3;
  font-family: "Segoe UI", "Microsoft JhengHei", sans-serif;
  font-size: 15px;
  line-height: 1.65;
}
.markdown-body { padding: 20px 24px 40px; max-width: 820px; }
h1, h2, h3, h4, h5, h6 { color: #7cdaf9; margin-top: 1.4em; margin-bottom: 0.5em; font-weight: 650; }
h1 { font-size: 1.9em; border-bottom: 1px solid #2a3648; padding-bottom: 0.25em; }
h2 { font-size: 1.5em; border-bottom: 1px solid #243041; padding-bottom: 0.2em; }
h3 { font-size: 1.25em; }
p { margin: 0.75em 0; }
a { color: #5ec8f0; text-decoration: none; }
a:hover { text-decoration: underline; }
code {
  font-family: Consolas, "Cascadia Mono", monospace;
  background: #1a2330;
  padding: 0.15em 0.4em;
  border-radius: 4px;
  font-size: 0.92em;
}
pre {
  background: #121a24;
  border: 1px solid #2a3648;
  border-radius: 8px;
  padding: 12px 14px;
  overflow: auto;
}
pre code { background: transparent; padding: 0; }
blockquote {
  margin: 1em 0;
  padding: 0.4em 1em;
  border-left: 4px solid #0e7490;
  background: #151c26;
  color: #c5d0dc;
}
table { border-collapse: collapse; width: 100%; margin: 1em 0; }
th, td { border: 1px solid #2a3648; padding: 8px 10px; }
th { background: #1a2330; }
img { max-width: 100%; height: auto; border-radius: 6px; }
.markdown-body::after { content: ""; display: table; clear: both; }
audio, video { width: 100%; max-width: 640px; margin: 1em 0; }
hr { border: none; border-top: 1px solid #2a3648; margin: 1.5em 0; }
ul, ol { padding-left: 1.4em; }
li { margin: 0.25em 0; }
li > input[type="checkbox"] { margin-right: 0.45em; }
strong { color: #fff; }
dl { margin: 1em 0; }
dt { font-weight: 700; color: #fff; }
dd { margin: 0.25em 0 0.75em 1.5em; color: #c5d0dc; }
mark { background: #fde68a; color: #111827; padding: 0.05em 0.2em; border-radius: 3px; }
kbd { font-family: Consolas, "Cascadia Mono", monospace; background: #111827; border: 1px solid #384558; border-bottom-width: 2px; border-radius: 4px; padding: 0.1em 0.35em; }
sub, sup { line-height: 0; }
figure { margin: 1.25em 0; }
figcaption { color: #9aa8b8; font-size: 0.9em; text-align: center; margin-top: 0.4em; }
.footnotes { color: #c5d0dc; font-size: 0.92em; border-top: 1px solid #2a3648; margin-top: 2em; padding-top: 0.75em; }
.footnote-ref, .footnote-backref { font-size: 0.85em; }
.math, .math-display { font-family: "Cambria Math", "STIX Two Math", serif; }
.math-display { display: block; overflow-x: auto; padding: 0.75em 1em; background: #121a24; border: 1px solid #2a3648; border-radius: 8px; }
.alert { border: 1px solid #2a3648; border-left-width: 4px; border-radius: 8px; padding: 0.75em 1em; background: #151c26; }
.alert-note { border-left-color: #5ec8f0; }
.alert-tip { border-left-color: #22c55e; }
.alert-warning { border-left-color: #f59e0b; }
.alert-important { border-left-color: #a78bfa; }
.alert-caution { border-left-color: #ef4444; }
.contains-task-list { list-style: none; padding-left: 0.4em; }
.task-list-item { list-style: none; }
.diagram, .mermaid { overflow-x: auto; }
""";
}
