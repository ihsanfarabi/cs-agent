using System.Text.RegularExpressions;

namespace CsAgent.Core;

/// <summary>Page identity: relative path (path mode) or absolute URL (crawl mode).</summary>
public sealed record LoadedPage(string Key, string Text);

public sealed record LoadReport(IReadOnlyList<LoadedPage> Pages, IReadOnlyList<string> Skipped);

/// <summary>
/// Recursive loader for local .md/.html files. Page identity = path relative to
/// the ingest root. Carries a symlink-cycle guard and a recursion depth cap.
/// Unreadable/binary files are skipped with a one-line warning, never fatal.
/// </summary>
public static class CorpusLoader
{
    private static readonly Regex HtmlScriptStyle = new(
        @"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HtmlTags = new("<[^>]+>", RegexOptions.Singleline);

    private const int MaxDepth = 16;

    public static LoadReport Load(string rootPath)
    {
        if (File.Exists(rootPath))
            throw new CsAgentException(new CsAgentError(
                "ingest", "path-not-directory", "Ingest expects a directory (path mode). Single-file ingest is not supported in v1."));
        if (!Directory.Exists(rootPath))
            throw new CsAgentException(new CsAgentError(
                "ingest", "path-not-found", $"Ingest path does not exist: '{rootPath}'."));
        if (!Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).Any(f =>
            Path.GetExtension(f).ToLowerInvariant() is ".md" or ".html"))
            throw new CsAgentException(new CsAgentError(
                "ingest", "no-crawlable-pages", $"No .md/.html files found under '{rootPath}'. Ingested nothing."));


        var root = Path.GetFullPath(rootPath);
        var pages = new List<LoadedPage>();
        var skipped = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        LoadDirectory(root, root, depth: 0, pages, skipped, visited);

        return new LoadReport(pages, skipped);
    }

    private static void LoadDirectory(
        string current, string root, int depth,
        List<LoadedPage> pages, List<string> skipped, HashSet<string> visited)
    {
        if (depth > MaxDepth)
        {
            skipped.Add($"{current} — recursion depth cap {MaxDepth} exceeded");
            return;
        }

        var real = Path.GetFullPath(current);
        var resolved = ResolveRealPath(real);
        if (!visited.Add(resolved))
        {
            skipped.Add($"{real} — symlink cycle detected");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(current).OrderBy(f => f, StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".md" or ".html")) continue;

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (LooksBinary(bytes))
                {
                    skipped.Add($"{relative} — binary file");
                    continue;
                }
                var text = File.ReadAllText(file);
                pages.Add(new LoadedPage(relative, ext == ".html" ? StripHtml(text) : text));
            }
            catch (Exception)
            {
                skipped.Add($"{relative} — unreadable (skipped, ingest continues)");
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(current).OrderBy(d => d, StringComparer.Ordinal))
            LoadDirectory(dir, root, depth + 1, pages, skipped, visited);
    }

    /// <summary>Heuristic: NUL byte or >10% non-printable = binary.</summary>
    internal static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        if (Array.IndexOf(bytes, (byte)0) >= 0) return true;
        var nonPrintable = 0;
        foreach (var b in bytes)
            if (b < 9 || (b > 13 && b < 32))
                nonPrintable++;
        return nonPrintable > bytes.Length / 10;
    }

    internal static string StripHtml(string html)
    {
        var withoutScripts = HtmlScriptStyle.Replace(html, " ");
        var withoutTags = HtmlTags.Replace(withoutScripts, "\n");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        var normalized = decoded.Replace(' ', ' ');
        return Regex.Replace(normalized, @"\n{3,}", "\n\n").Trim();
    }

    private static string ResolveRealPath(string path)
    {
        // Follow symlink chains without extra dependencies; bounded hops.
        var resolved = path;
        for (var hop = 0; hop < 8; hop++)
        {
            var target = Directory.Exists(resolved)
                ? new DirectoryInfo(resolved).LinkTarget
                : new FileInfo(resolved).LinkTarget;
            if (target is null) break;
            resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resolved)!, target));
        }
        return resolved;
    }
}