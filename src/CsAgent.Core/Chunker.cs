namespace CsAgent.Core;

/// <summary>
/// Heading-aware fixed-window chunker: chunk boundaries never merge across
/// markdown heading sections; within a section, fixed windows with overlap.
/// </summary>
public static class Chunker
{
    public static IReadOnlyList<string> Chunk(string text, int chunkSize, int overlap)
    {
        if (chunkSize < 200)
            throw new CsAgentException(new CsAgentError(
                "chunker", "invalid-chunk-size", $"chunk_size must be >= 200; got {chunkSize}."));
        if (overlap < 0 || overlap >= chunkSize)
            throw new CsAgentException(new CsAgentError(
                "chunker", "invalid-overlap", $"overlap must be in [0, chunk_size); got {overlap} for size {chunkSize}."));

        var chunks = new List<string>();
        foreach (var section in SplitOnHeadings(text))
        {
            var trimmed = section.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Length <= chunkSize)
            {
                chunks.Add(trimmed);
                continue;
            }
            var start = 0;
            while (start < trimmed.Length)
            {
                var end = Math.Min(start + chunkSize, trimmed.Length);
                chunks.Add(trimmed[start..end]);
                if (end == trimmed.Length) break;
                start = end - overlap;
            }
        }
        return chunks;
    }

    private static IEnumerable<string> SplitOnHeadings(string text)
    {
        var lines = text.Split('\n');
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                if (current.Count > 0)
                {
                    yield return string.Join('\n', current);
                    current.Clear();
                }
            }
            current.Add(line);
        }
        if (current.Count > 0)
            yield return string.Join('\n', current);
    }
}