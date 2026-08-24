using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Hexoer.Services;

/// <summary>
/// Converts Markdown body ↔ contenteditable HTML for the WYSIWYG editor.
/// Front matter is stripped on the way in; callers rejoin it on the way out.
/// </summary>
public static partial class MarkdownWysiwygConverter
{
    public static string ToEditableHtml(string markdown)
    {
        var body = MarkdownPreviewService.StripFrontMatter(markdown ?? string.Empty);
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var (protectedMarkdown, tokens) = ProtectShortcodes(body);
        var html = MarkdownPreviewService.ToHtmlFragment(protectedMarkdown);
        html = RestoreShortcodesInHtml(html, tokens);
        html = EnableTaskCheckboxes(html);
        return html;
    }

    public static string FromEditableHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var document = new HtmlDocument
        {
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        document.LoadHtml(html);
        var root = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;

        var builder = new StringBuilder();
        WriteBlocks(root.ChildNodes, builder);
        return NormalizeMarkdown(builder.ToString());
    }

    private static void WriteBlocks(HtmlNodeCollection nodes, StringBuilder builder)
    {
        var inlineBuffer = new StringBuilder();

        void FlushInlines()
        {
            if (inlineBuffer.Length == 0) return;
            var text = inlineBuffer.ToString().Trim();
            inlineBuffer.Clear();
            if (text.Length == 0) return;
            AppendBlock(builder, text);
        }

        foreach (var node in nodes)
        {
            if (node.NodeType == HtmlNodeType.Comment)
                continue;

            if (node.NodeType == HtmlNodeType.Text)
            {
                var text = CollapseWhitespace(HtmlEntity.DeEntitize(node.InnerText));
                if (!string.IsNullOrWhiteSpace(text))
                    inlineBuffer.Append(text);
                continue;
            }

            var name = Name(node);
            if (name is "script" or "style" or "iframe" or "object" or "embed")
                continue;

            if (IsBlock(name))
            {
                FlushInlines();
                WriteBlock(node, builder);
            }
            else
            {
                WriteInline(node, inlineBuffer);
            }
        }

        FlushInlines();
    }

    private static void WriteBlock(HtmlNode node, StringBuilder builder)
    {
        var name = Name(node);
        switch (name)
        {
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                var level = name[1] - '0';
                AppendBlock(builder, new string('#', level) + " " + InlinesToString(node).Trim());
                return;
            case "p":
            case "div":
            case "section":
            case "article":
            case "figure":
            case "figcaption":
                if (HasBlockChild(node))
                {
                    WriteBlocks(node.ChildNodes, builder);
                    return;
                }

                if (IsVisuallyEmpty(node))
                    return;
                AppendBlock(builder, InlinesToString(node).Trim());
                return;
            case "blockquote":
                var quoted = BlocksToString(node.ChildNodes);
                if (quoted.Length == 0)
                    quoted = InlinesToString(node).Trim();
                if (quoted.Length == 0)
                    return;
                var quotedBuilder = new StringBuilder();
                foreach (var line in quoted.Replace("\r\n", "\n").Split('\n'))
                    quotedBuilder.Append("> ").Append(line).Append('\n');
                AppendBlock(builder, quotedBuilder.ToString());
                return;
            case "ul":
            case "ol":
                var listBuilder = new StringBuilder();
                WriteList(node, listBuilder, indent: 0, ordered: name == "ol");
                AppendBlock(builder, listBuilder.ToString());
                return;
            case "pre":
                WriteCodeBlock(node, builder);
                return;
            case "table":
                WriteTable(node, builder);
                return;
            case "hr":
                AppendBlock(builder, "---");
                return;
            case "audio":
            case "video":
                AppendBlock(builder, MediaHtml(node));
                return;
            case "br":
                builder.Append("  \n");
                return;
            default:
                if (HasBlockChild(node))
                    WriteBlocks(node.ChildNodes, builder);
                else if (!IsVisuallyEmpty(node))
                    AppendBlock(builder, InlinesToString(node).Trim());
                return;
        }
    }

    private static void WriteList(HtmlNode list, StringBuilder builder, int indent, bool ordered)
    {
        var index = 1;
        foreach (var li in list.ChildNodes)
        {
            if (Name(li) != "li")
            {
                if (Name(li) is "ul" or "ol")
                    WriteList(li, builder, indent + 2, Name(li) == "ol");
                continue;
            }

            var checkbox = OwnCheckbox(li);
            string marker;
            if (checkbox is not null)
            {
                var box = IsChecked(checkbox) ? "[x]" : "[ ]";
                marker = ordered ? $"{index}. {box} " : $"- {box} ";
            }
            else
            {
                marker = ordered ? $"{index}. " : "- ";
            }

            if (ordered)
                index++;

            var inline = new StringBuilder();
            HtmlNode? nested = null;
            foreach (var child in li.ChildNodes)
            {
                var childName = Name(child);
                if (childName is "ul" or "ol")
                {
                    nested = child;
                    continue;
                }

                if (child.NodeType == HtmlNodeType.Text)
                {
                    inline.Append(CollapseWhitespace(HtmlEntity.DeEntitize(child.InnerText)));
                    continue;
                }

                if (childName == "p")
                {
                    if (inline.Length > 0)
                        inline.Append(' ');
                    WriteInlines(child, inline);
                    continue;
                }

                if (childName == "input" && IsCheckbox(child))
                    continue;

                WriteInline(child, inline);
            }

            builder.Append(' ', indent).Append(marker).Append(inline.ToString().Trim()).Append('\n');
            if (nested is not null)
                WriteList(nested, builder, indent + 2, Name(nested) == "ol");
        }
    }

    private static void WriteCodeBlock(HtmlNode pre, StringBuilder builder)
    {
        var code = pre.SelectSingleNode("./code") ?? pre;
        var language = "";
        var className = code.GetAttributeValue("class", "") + " " + pre.GetAttributeValue("class", "");
        var languageMatch = LanguageClassRegex().Match(className);
        if (languageMatch.Success)
            language = languageMatch.Groups[1].Value;

        var text = HtmlEntity.DeEntitize(code.InnerText ?? string.Empty).Replace("\r\n", "\n");
        if (text.StartsWith('\n'))
            text = text[1..];
        if (text.EndsWith('\n'))
            text = text[..^1];

        var fence = "```";
        while (text.Contains(fence, StringComparison.Ordinal))
            fence += "`";

        AppendBlock(builder, $"{fence}{language}\n{text}\n{fence}");
    }

    private static void WriteTable(HtmlNode table, StringBuilder builder)
    {
        var rows = table.SelectNodes(".//tr");
        if (rows is null || rows.Count == 0)
            return;

        var parsed = new List<List<string>>();
        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./th|./td");
            if (cells is null)
                continue;
            parsed.Add(cells.Select(cell => InlinesToString(cell).Trim().Replace("|", "\\|", StringComparison.Ordinal)).ToList());
        }

        if (parsed.Count == 0)
            return;

        var columns = parsed.Max(row => row.Count);
        if (columns == 0)
            return;

        foreach (var row in parsed)
        {
            while (row.Count < columns)
                row.Add(string.Empty);
        }

        var tableBuilder = new StringBuilder();
        tableBuilder.Append("| ").Append(string.Join(" | ", parsed[0])).Append(" |\n");
        tableBuilder.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", columns))).Append(" |\n");
        foreach (var row in parsed.Skip(1))
            tableBuilder.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
        AppendBlock(builder, tableBuilder.ToString());
    }

    private static string BlocksToString(HtmlNodeCollection nodes)
    {
        var builder = new StringBuilder();
        WriteBlocks(nodes, builder);
        return NormalizeMarkdown(builder.ToString());
    }

    private static string InlinesToString(HtmlNode node)
    {
        var builder = new StringBuilder();
        WriteInlines(node, builder);
        return builder.ToString();
    }

    private static void WriteInlines(HtmlNode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
            WriteInline(child, builder);
    }

    private static void WriteInline(HtmlNode node, StringBuilder builder)
    {
        if (node.NodeType == HtmlNodeType.Comment)
            return;

        if (node.NodeType == HtmlNodeType.Text)
        {
            builder.Append(CollapseWhitespace(HtmlEntity.DeEntitize(node.InnerText)));
            return;
        }

        var name = Name(node);
        switch (name)
        {
            case "br":
                builder.Append("  \n");
                return;
            case "strong":
            case "b":
                WrapInline(node, builder, "**", "**");
                return;
            case "em":
            case "i":
                WrapInline(node, builder, "*", "*");
                return;
            case "del":
            case "s":
            case "strike":
                WrapInline(node, builder, "~~", "~~");
                return;
            case "code":
                if (Name(node.ParentNode) == "pre")
                    return;
                builder.Append('`').Append(HtmlEntity.DeEntitize(node.InnerText)).Append('`');
                return;
            case "a":
                var href = node.GetAttributeValue("href", "");
                var label = InlinesToString(node).Trim();
                if (string.IsNullOrWhiteSpace(href))
                {
                    builder.Append(label);
                    return;
                }

                if (label.Length == 0)
                    label = href;
                builder.Append('[').Append(label).Append("](").Append(href).Append(')');
                return;
            case "img":
                WriteImage(node, builder);
                return;
            case "audio":
            case "video":
                builder.Append(MediaHtml(node));
                return;
            case "input":
                return;
            case "span":
            case "font":
            case "u":
            case "mark":
                WriteStyledSpan(node, builder);
                return;
            default:
                if (IsBlock(name))
                    WriteBlock(node, builder);
                else
                    WriteInlines(node, builder);
                return;
        }
    }

    private static void WriteStyledSpan(HtmlNode node, StringBuilder builder)
    {
        var style = node.GetAttributeValue("style", "") ?? string.Empty;
        var className = node.GetAttributeValue("class", "") ?? string.Empty;
        var inner = InlinesToString(node);
        if (LooksBold(style, className))
            inner = $"**{inner.Trim()}**";
        if (LooksItalic(style, className))
            inner = $"*{inner.Trim()}*";
        if (LooksStrike(style, className))
            inner = $"~~{inner.Trim()}~~";
        builder.Append(inner);
    }

    private static void WrapInline(HtmlNode node, StringBuilder builder, string prefix, string suffix)
    {
        var inner = InlinesToString(node);
        if (string.IsNullOrWhiteSpace(inner))
            return;
        var leading = inner.Length - inner.TrimStart().Length;
        var trailing = inner.Length - inner.TrimEnd().Length;
        if (leading > 0)
            builder.Append(inner[..leading]);
        builder.Append(prefix).Append(inner.Trim()).Append(suffix);
        if (trailing > 0)
            builder.Append(inner[^trailing..]);
    }

    private static bool LooksBold(string style, string className) =>
        className.Contains("bold", StringComparison.OrdinalIgnoreCase)
        || style.Contains("font-weight", StringComparison.OrdinalIgnoreCase)
           && (style.Contains("bold", StringComparison.OrdinalIgnoreCase)
               || style.Contains("700", StringComparison.Ordinal)
               || style.Contains("800", StringComparison.Ordinal)
               || style.Contains("900", StringComparison.Ordinal));

    private static bool LooksItalic(string style, string className) =>
        className.Contains("italic", StringComparison.OrdinalIgnoreCase)
        || style.Contains("italic", StringComparison.OrdinalIgnoreCase);

    private static bool LooksStrike(string style, string className) =>
        className.Contains("line-through", StringComparison.OrdinalIgnoreCase)
        || style.Contains("line-through", StringComparison.OrdinalIgnoreCase);

    private static HtmlNode? OwnCheckbox(HtmlNode li)
    {
        foreach (var input in li.ChildNodes)
        {
            if (IsCheckbox(input))
                return input;
            if (Name(input) == "p")
            {
                foreach (var nested in input.ChildNodes)
                {
                    if (IsCheckbox(nested))
                        return nested;
                }
            }
        }

        return null;
    }

    private static bool IsCheckbox(HtmlNode node) =>
        Name(node) == "input"
        && node.GetAttributeValue("type", "").Equals("checkbox", StringComparison.OrdinalIgnoreCase);

    private static bool IsChecked(HtmlNode input) =>
        input.Attributes["checked"] is not null
        || input.GetAttributeValue("aria-checked", "").Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlock(string name) => name is
        "p" or "div" or "section" or "article" or "figure" or "figcaption"
        or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
        or "ul" or "ol" or "li" or "pre" or "blockquote"
        or "table" or "thead" or "tbody" or "tr" or "hr"
        or "audio" or "video";

    private static void WriteImage(HtmlNode node, StringBuilder builder)
    {
        var src = ResolveMediaSrc(node);
        if (string.IsNullOrWhiteSpace(src))
            return;

        var alt = node.GetAttributeValue("alt", "");
        var width = ReadImageWidth(node);
        var align = ReadImageAlign(node);
        if (width is null && string.IsNullOrEmpty(align))
        {
            builder.Append("![").Append(alt).Append("](").Append(src).Append(')');
            return;
        }

        builder.Append("<img src=\"").Append(WebUtility.HtmlEncode(src))
            .Append("\" alt=\"").Append(WebUtility.HtmlEncode(alt)).Append('"');
        if (width is { } pixels)
            builder.Append(" width=\"").Append(pixels).Append('"');
        if (!string.IsNullOrEmpty(align))
            builder.Append(" data-hugoer-align=\"").Append(align).Append('"');
        var style = BuildImageStyle(width, align);
        if (style.Length > 0)
            builder.Append(" style=\"").Append(style).Append('"');
        builder.Append(" />");
    }

    private static int? ReadImageWidth(HtmlNode node)
    {
        if (TryParsePx(node.GetAttributeValue("width", ""), out var fromAttr))
            return fromAttr;

        var style = node.GetAttributeValue("style", "") ?? string.Empty;
        var match = StyleWidthRegex().Match(style);
        if (match.Success && TryParsePx(match.Groups["px"].Value, out var fromStyle))
            return fromStyle;
        return null;
    }

    private static string? ReadImageAlign(HtmlNode node)
    {
        var marked = node.GetAttributeValue("data-hugoer-align", "");
        if (IsImageAlign(marked))
            return marked;

        var style = node.GetAttributeValue("style", "") ?? string.Empty;
        var className = node.GetAttributeValue("class", "") ?? string.Empty;
        var combined = style + " " + className;
        if (LooksFloat(combined, "left") || combined.Contains("wrap-left", StringComparison.OrdinalIgnoreCase))
            return "wrap-left";
        if (LooksFloat(combined, "right") || combined.Contains("wrap-right", StringComparison.OrdinalIgnoreCase))
            return "wrap-right";
        if (LooksCentered(style))
            return "center";
        if (LooksRightAligned(style))
            return "right";
        if (LooksLeftAligned(style))
            return "left";

        var parentStyle = node.ParentNode?.GetAttributeValue("style", "") ?? string.Empty;
        if (parentStyle.Contains("text-align:center", StringComparison.OrdinalIgnoreCase)
            || parentStyle.Contains("text-align: center", StringComparison.OrdinalIgnoreCase))
            return "center";
        if (parentStyle.Contains("text-align:right", StringComparison.OrdinalIgnoreCase)
            || parentStyle.Contains("text-align: right", StringComparison.OrdinalIgnoreCase))
            return "right";
        if (parentStyle.Contains("text-align:left", StringComparison.OrdinalIgnoreCase)
            || parentStyle.Contains("text-align: left", StringComparison.OrdinalIgnoreCase))
            return "left";
        return null;
    }

    private static string BuildImageStyle(int? widthPx, string? align)
    {
        var parts = new List<string> { "max-width:100%", "height:auto" };
        if (widthPx is { } width and > 0)
            parts.Add($"width:{width}px");

        switch (align)
        {
            case "center":
                parts.Add("display:block");
                parts.Add("float:none");
                parts.Add("margin:0.5em auto");
                break;
            case "right":
                parts.Add("display:block");
                parts.Add("float:none");
                parts.Add("margin:0.5em 0 0.5em auto");
                break;
            case "left":
                parts.Add("display:block");
                parts.Add("float:none");
                parts.Add("margin:0.5em auto 0.5em 0");
                break;
            case "wrap-left":
                parts.Add("display:block");
                parts.Add("float:left");
                parts.Add("margin:0.25em 1em 0.8em 0");
                break;
            case "wrap-right":
                parts.Add("display:block");
                parts.Add("float:right");
                parts.Add("margin:0.25em 0 0.8em 1em");
                break;
        }

        return string.Join(";", parts);
    }

    private static bool IsImageAlign(string value) =>
        value is "left" or "center" or "right" or "wrap-left" or "wrap-right";

    private static bool LooksFloat(string css, string side) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            css, $@"float\s*:\s*{side}", RegexOptions.IgnoreCase);

    private static bool LooksCentered(string style)
    {
        if (style.Contains("0.5em auto 0.5em 0", StringComparison.OrdinalIgnoreCase)
            || style.Contains("0.5em 0 0.5em auto", StringComparison.OrdinalIgnoreCase))
            return false;
        return style.Contains("margin:0.5em auto", StringComparison.OrdinalIgnoreCase)
            || style.Contains("margin: 0.5em auto", StringComparison.OrdinalIgnoreCase)
            || style.Contains("margin:0 auto", StringComparison.OrdinalIgnoreCase)
            || style.Contains("margin: 0 auto", StringComparison.OrdinalIgnoreCase)
            || (style.Contains("margin-left:auto", StringComparison.OrdinalIgnoreCase)
                && style.Contains("margin-right:auto", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksRightAligned(string style) =>
        style.Contains("0.5em 0 0.5em auto", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLeftAligned(string style) =>
        style.Contains("0.5em auto 0.5em 0", StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePx(string value, out int pixels)
    {
        pixels = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2].Trim();
        return int.TryParse(trimmed, out pixels) && pixels > 0;
    }

    private static string MediaHtml(HtmlNode node)
    {
        var name = Name(node);
        var src = ResolveMediaSrc(node);
        return $"<{name} controls src=\"{src}\"></{name}>";
    }

    private static string ResolveMediaSrc(HtmlNode node)
    {
        var marked = node.GetAttributeValue("data-hugoer-src", "");
        if (!string.IsNullOrWhiteSpace(marked) && !IsTransientPreviewSrc(marked))
            return marked;

        var src = node.GetAttributeValue("src", "");
        return IsTransientPreviewSrc(src) ? string.Empty : src;
    }

    private static bool IsTransientPreviewSrc(string src) =>
        src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || src.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);

    private static bool HasBlockChild(HtmlNode node) =>
        node.ChildNodes.Any(child => child.NodeType == HtmlNodeType.Element && IsBlock(Name(child)));

    private static bool IsVisuallyEmpty(HtmlNode node)
    {
        if (node.SelectSingleNode(".//img|.//hr|.//table|.//input|.//audio|.//video") is not null)
            return false;
        return string.IsNullOrWhiteSpace(node.InnerText);
    }

    private static void AppendBlock(StringBuilder builder, string block)
    {
        var trimmed = block.TrimEnd('\r', '\n');
        if (trimmed.Length == 0)
            return;
        if (builder.Length > 0)
        {
            if (builder[^1] != '\n')
                builder.Append('\n');
            builder.Append('\n');
        }

        builder.Append(trimmed).Append('\n');
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return WhitespaceRegex().Replace(text, " ");
    }

    private static string NormalizeMarkdown(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = TrailingWhitespaceRegex().Replace(text, match =>
        {
            var prefix = match.Groups[1].Value;
            return prefix == "  " ? "  \n" : "\n";
        });
        text = ExtraBlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    private static string Name(HtmlNode node) => node.Name.ToLowerInvariant();

    private static (string Markdown, List<string> Tokens) ProtectShortcodes(string markdown)
    {
        var tokens = new List<string>();
        var protectedMarkdown = ShortcodeRegex().Replace(markdown, match =>
        {
            var index = tokens.Count;
            tokens.Add(match.Value);
            return $"@@HUGOERSC{index}@@";
        });
        return (protectedMarkdown, tokens);
    }

    private static string RestoreShortcodesInHtml(string html, List<string> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            var placeholder = $"@@HUGOERSC{index}@@";
            var encoded = WebUtility.HtmlEncode(tokens[index]);
            html = html.Replace(placeholder, encoded, StringComparison.Ordinal);
        }

        return html;
    }

    private static string EnableTaskCheckboxes(string html) =>
        DisabledCheckboxRegex().Replace(html, "");

    [GeneratedRegex(@"\{\{[<%][\s\S]*?[%>]\}\}")]
    private static partial Regex ShortcodeRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"([ \t]*)\n")]
    private static partial Regex TrailingWhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLinesRegex();

    [GeneratedRegex(@"(?:language-|lang-)([A-Za-z0-9_+-]+)")]
    private static partial Regex LanguageClassRegex();

    [GeneratedRegex(@"width\s*:\s*(?<px>\d+)\s*px", RegexOptions.IgnoreCase)]
    private static partial Regex StyleWidthRegex();

    [GeneratedRegex(@"\sdisabled(?:=([""']?)disabled\1|=([""']?)\2)?", RegexOptions.IgnoreCase)]
    private static partial Regex DisabledCheckboxRegex();
}
