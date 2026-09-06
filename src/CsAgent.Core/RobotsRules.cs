namespace CsAgent.Core;

/// <summary>
/// robots.txt rules for one crawl: prefix Disallow ONLY (no wildcards, Allow
/// ignored in v1 — documented limitation); the User-agent: * group and an
/// explicit cs-agent group apply as a union; a larger Crawl-delay wins.
/// Empty Disallow / absent body = allow-all.
/// </summary>
public sealed class RobotsRules
{
    private readonly IReadOnlyList<string> _disallowPrefixes;

    public TimeSpan? CrawlDelay { get; }

    internal RobotsRules(IReadOnlyList<string> disallowPrefixes, TimeSpan? crawlDelay)
    {
        _disallowPrefixes = disallowPrefixes;
        CrawlDelay = crawlDelay;
    }

    public bool IsAllowed(string path) =>
        !_disallowPrefixes.Any(path.StartsWith);

    public static RobotsRules Parse(string body)
    {
        var prefixes = new List<string>();
        TimeSpan? delay = null;
        var inTarget = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.AsSpan();
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            var field = trimmed[..colon].Trim().ToString().ToLowerInvariant();
            var value = trimmed[(colon + 1)..].Trim().ToString();
            switch (field)
            {
                case "user-agent":
                    inTarget = value is "*" or "cs-agent";
                    break;
                case "disallow":
                    // empty Disallow = allow-all: contributes nothing
                    if (inTarget && value.Length > 0) prefixes.Add(value);
                    break;
                case "crawl-delay":
                    if (inTarget && int.TryParse(value, out var seconds) && seconds > 0)
                    {
                        var parsed = TimeSpan.FromSeconds(seconds);
                        if (delay is null || parsed > delay) delay = parsed;
                    }
                    break;
            }
        }
        return new RobotsRules(prefixes, delay);
    }
}