using System.Text.RegularExpressions;

namespace Hexoer.Services;

public readonly record struct MarkdownEditResult(
    string Text,
    int SelectionStart,
    int SelectionLength);

public static partial class MarkdownEditingService
{
    public static bool IsInsideFrontMatter(string text, int position)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var delimiter = normalized.StartsWith("+++\n", StringComparison.Ordinal) ? "+++"
            : normalized.StartsWith("---\n", StringComparison.Ordinal) ? "---"
            : null;
        if (delimiter is null) return false;
        var end = normalized.IndexOf($"\n{delimiter}\n", delimiter.Length + 1, StringComparison.Ordinal);
        return end >= 0 && position <= end + delimiter.Length + 2;
    }

    public static MarkdownEditResult Wrap(
        string text,
        int selectionStart,
        int selectionLength,
        string prefix,
        string suffix,
        string placeholder)
    {
        var (start, length) = Normalize(text, selectionStart, selectionLength);
        if (length > 0
            && start >= prefix.Length
            && start + length + suffix.Length <= text.Length
            && text.AsSpan(start - prefix.Length, prefix.Length).SequenceEqual(prefix)
            && text.AsSpan(start + length, suffix.Length).SequenceEqual(suffix))
        {
            var unwrapped = text.Remove(start + length, suffix.Length).Remove(start - prefix.Length, prefix.Length);
            return new MarkdownEditResult(unwrapped, start - prefix.Length, length);
        }

        var selected = length > 0 ? text.Substring(start, length) : placeholder;
        if (length > 0
            && selected.StartsWith(prefix, StringComparison.Ordinal)
            && selected.EndsWith(suffix, StringComparison.Ordinal)
            && selected.Length >= prefix.Length + suffix.Length)
        {
            var inner = selected[prefix.Length..^suffix.Length];
            var unwrapped = text.Remove(start, length).Insert(start, inner);
            return new MarkdownEditResult(unwrapped, start, inner.Length);
        }

        var replacement = prefix + selected + suffix;
        var result = text.Remove(start, length).Insert(start, replacement);
        return new MarkdownEditResult(result, start + prefix.Length, selected.Length);
    }

    public static MarkdownEditResult PrefixLines(
        string text,
        int selectionStart,
        int selectionLength,
        string prefix)
    {
        var range = GetLineRange(text, selectionStart, selectionLength);
        var block = text.Substring(range.Start, range.Length);
        var lines = block.Split('\n');
        var nonEmpty = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var remove = nonEmpty.Length > 0 && nonEmpty.All(line => line.StartsWith(prefix, StringComparison.Ordinal));
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            lines[index] = remove ? lines[index][prefix.Length..] : prefix + lines[index];
        }
        return ReplaceBlock(text, range.Start, range.Length, string.Join('\n', lines));
    }

    public static MarkdownEditResult Heading(
        string text,
        int selectionStart,
        int selectionLength,
        int level)
    {
        level = Math.Clamp(level, 1, 6);
        var range = GetLineRange(text, selectionStart, selectionLength);
        var prefix = new string('#', level) + " ";
        var lines = text.Substring(range.Start, range.Length).Split('\n');
        var already = lines.Where(line => !string.IsNullOrWhiteSpace(line))
            .All(line => line.StartsWith(prefix, StringComparison.Ordinal));
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            var clean = HeadingPrefixRegex().Replace(lines[index], string.Empty);
            lines[index] = already ? clean : prefix + clean;
        }
        return ReplaceBlock(text, range.Start, range.Length, string.Join('\n', lines));
    }

    public static MarkdownEditResult OrderedList(string text, int selectionStart, int selectionLength)
    {
        var range = GetLineRange(text, selectionStart, selectionLength);
        var lines = text.Substring(range.Start, range.Length).Split('\n');
        var nonEmpty = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var remove = nonEmpty.Length > 0 && nonEmpty.All(line => OrderedPrefixRegex().IsMatch(line));
        var number = 1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            var clean = OrderedPrefixRegex().Replace(lines[index], string.Empty);
            lines[index] = remove ? clean : $"{number++}. {clean}";
        }
        return ReplaceBlock(text, range.Start, range.Length, string.Join('\n', lines));
    }

    public static MarkdownEditResult CodeBlock(string text, int selectionStart, int selectionLength)
    {
        var (start, length) = Normalize(text, selectionStart, selectionLength);
        var selected = length > 0 ? text.Substring(start, length).Trim('\r', '\n') : "程式碼";
        var block = $"```text\n{selected}\n```";
        return InsertBlock(text, start, length, block, 8, selected.Length);
    }

    public static MarkdownEditResult Link(string text, int selectionStart, int selectionLength, bool image)
    {
        var (start, length) = Normalize(text, selectionStart, selectionLength);
        var label = length > 0
            ? text.Substring(start, length)
            : image ? "圖片說明" : "連結文字";
        var destination = image ? "image.jpg" : "https://";
        var replacement = $"{(image ? "!" : string.Empty)}[{label}]({destination})";
        var result = text.Remove(start, length).Insert(start, replacement);
        var destinationStart = start + (image ? 2 : 1) + label.Length + 2;
        return new MarkdownEditResult(result, destinationStart, destination.Length);
    }

    public static MarkdownEditResult InsertTable(string text, int selectionStart, int selectionLength) =>
        InsertBlock(
            text,
            selectionStart,
            selectionLength,
            "| 欄位一 | 欄位二 |\n| --- | --- |\n| 內容 | 內容 |",
            2,
            3);

    public static MarkdownEditResult HorizontalRule(string text, int selectionStart, int selectionLength) =>
        InsertBlock(text, selectionStart, selectionLength, "---", 3, 0);

    public static MarkdownEditResult InsertSnippet(
        string text,
        int selectionStart,
        int selectionLength,
        string snippet)
    {
        if (string.IsNullOrEmpty(snippet))
            return new MarkdownEditResult(text, selectionStart, 0);
        return InsertBlock(text, selectionStart, selectionLength, snippet, 0, snippet.Length);
    }

    private static MarkdownEditResult InsertBlock(
        string text,
        int selectionStart,
        int selectionLength,
        string block,
        int innerOffset,
        int innerLength)
    {
        var (start, length) = Normalize(text, selectionStart, selectionLength);
        var before = start > 0 && text[start - 1] != '\n' ? "\n\n" : string.Empty;
        var afterIndex = start + length;
        var after = afterIndex < text.Length && text[afterIndex] != '\n' ? "\n\n" : string.Empty;
        var replacement = before + block + after;
        var result = text.Remove(start, length).Insert(start, replacement);
        return new MarkdownEditResult(result, start + before.Length + innerOffset, innerLength);
    }

    private static MarkdownEditResult ReplaceBlock(string text, int start, int length, string replacement)
    {
        var result = text.Remove(start, length).Insert(start, replacement);
        return new MarkdownEditResult(result, start, replacement.Length);
    }

    private static (int Start, int Length) GetLineRange(string text, int selectionStart, int selectionLength)
    {
        var (start, length) = Normalize(text, selectionStart, selectionLength);
        var lineStart = start <= 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var selectedEnd = start + length;
        var lineEnd = selectedEnd >= text.Length ? text.Length : text.IndexOf('\n', selectedEnd);
        if (lineEnd < 0) lineEnd = text.Length;
        return (lineStart, lineEnd - lineStart);
    }

    private static (int Start, int Length) Normalize(string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        return (start, length);
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+")]
    private static partial Regex OrderedPrefixRegex();
}
