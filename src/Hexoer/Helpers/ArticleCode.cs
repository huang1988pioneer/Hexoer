using System.Globalization;
using System.Text.RegularExpressions;

namespace Hexoer.Helpers;

public static partial class ArticleCode
{
    /// <summary>
    /// Suggested article stem: Gregorian yyyyMMdd plus a per-day sequence, e.g. 20260823-1.
    /// </summary>
    public static string Format(DateTimeOffset date, int number) =>
        $"{date:yyyyMMdd}-{Math.Max(1, number)}";

    public static string NextFromNames(IEnumerable<string> fileOrFolderNames, DateTimeOffset date)
    {
        var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var max = 0;
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in fileOrFolderNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var stem = Path.GetFileNameWithoutExtension(raw.Trim().TrimEnd('/', '\\'));
            if (string.IsNullOrWhiteSpace(stem)) continue;
            stems.Add(stem);

            var match = CodeRegex().Match(stem);
            if (!match.Success || match.Groups["date"].Value != day)
                continue;
            if (int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && n > max)
                max = n;
        }

        var next = max + 1;
        string code;
        do
        {
            code = Format(date, next);
            next++;
        } while (stems.Contains(code));

        return code;
    }

    public static string NextInDirectory(string? directory, DateTimeOffset date)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Format(date, 1);

        try
        {
            var names = Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!);
            return NextFromNames(names, date);
        }
        catch (IOException)
        {
            return Format(date, 1);
        }
        catch (UnauthorizedAccessException)
        {
            return Format(date, 1);
        }
    }

    [GeneratedRegex(@"^(?<date>\d{8})-(?<n>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();
}
