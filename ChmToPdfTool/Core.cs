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

public static partial class TocParser
{
    public static IReadOnlyList<string> ParseLocalPaths(string content)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in LocalParameterRegex().Matches(content))
        {
            var capturedValue = match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Value;
            var path = Normalize(capturedValue);
            if (path.Length > 0 && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
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

    [GeneratedRegex(
        "<param\\s+[^>]*name\\s*=\\s*[\\\"']?Local[\\\"']?[^>]*value\\s*=\\s*[\\\"']([^\\\"']+)[\\\"'][^>]*>|<param\\s+[^>]*value\\s*=\\s*[\\\"']([^\\\"']+)[\\\"'][^>]*name\\s*=\\s*[\\\"']?Local[\\\"']?[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalParameterRegex();
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
