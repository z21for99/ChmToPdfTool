using System.Net;
using System.Text.RegularExpressions;

namespace ChmToPdfTool;

public sealed class PauseController : IDisposable
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool>? _resumeSource;

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _resumeSource is not null;
            }
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            _resumeSource ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? source;
        lock (_sync)
        {
            source = _resumeSource;
            _resumeSource = null;
        }

        source?.TrySetResult(true);
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync)
        {
            task = _resumeSource?.Task ?? Task.CompletedTask;
        }

        return task.WaitAsync(cancellationToken);
    }

    public void Dispose() => Resume();
}

public sealed record TocEntry(string Title, string Path);

public static partial class TocParser
{
    public static IReadOnlyList<TocEntry> ParseEntries(string content)
    {
        var entries = new List<TocEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SitemapRegex().Matches(content))
        {
            var block = match.Groups[1].Value;
            var local = ReadParameter(block, "Local");
            if (local is null)
            {
                continue;
            }

            var path = Normalize(local);
            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            var title = ReadParameter(block, "Name") ?? string.Empty;
            entries.Add(new TocEntry(WebUtility.HtmlDecode(title).Trim(), path));
        }

        return entries;
    }

    public static IReadOnlyList<string> ParseLocalPaths(string content) =>
        ParseEntries(content).Select(entry => entry.Path).ToList();

    private static string? ReadParameter(string block, string parameterName)
    {
        foreach (Match match in ParameterRegex().Matches(block))
        {
            string? name = null;
            string? value = null;

            foreach (Match attribute in AttributeRegex().Matches(match.Value))
            {
                var attributeName = attribute.Groups["name"].Value;
                var attributeValue = attribute.Groups["value"].Success
                    ? attribute.Groups["value"].Value
                    : attribute.Groups["unquoted"].Value;

                if (string.Equals(attributeName, "name", StringComparison.OrdinalIgnoreCase))
                {
                    name = attributeValue;
                }
                else if (string.Equals(attributeName, "value", StringComparison.OrdinalIgnoreCase))
                {
                    value = attributeValue;
                }
            }

            if (value is not null && string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string Normalize(string value)
    {
        var path = WebUtility.HtmlDecode(value).Trim().Trim('"', '\'');
        var fragment = path.IndexOfAny(['#', '?']);
        if (fragment >= 0)
        {
            path = path[..fragment];
        }

        if (path.StartsWith("mk:@MSITStore:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = path.IndexOf("::/", StringComparison.Ordinal);
            path = separator >= 0 ? path[(separator + 3)..] : string.Empty;
        }

        path = Uri.UnescapeDataString(path).Replace('\\', '/');
        return path.TrimStart('/');
    }

    [GeneratedRegex(@"<object\b[^>]*>(.*?)</object>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SitemapRegex();

    [GeneratedRegex(@"<param\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParameterRegex();

    [GeneratedRegex(
        "(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<unquoted>[^\\s>]+))",
        RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();
}

public static class PageDiscovery
{
    private static readonly string[] Extensions = [".htm", ".html", ".xhtml"];

    public static IReadOnlyList<string> Discover(
        string rootDirectory,
        IReadOnlyList<string> orderedRelativePaths)
    {
        var root = Path.GetFullPath(rootDirectory);
        var pages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in orderedRelativePaths)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate) ||
                !IsHtml(candidate) ||
                !seen.Add(candidate))
            {
                continue;
            }

            pages.Add(candidate);
        }

        foreach (var candidate in Directory
                     .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(IsHtml)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(candidate);
            if (seen.Add(fullPath))
            {
                pages.Add(fullPath);
            }
        }

        return pages;
    }

    private static bool IsHtml(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
