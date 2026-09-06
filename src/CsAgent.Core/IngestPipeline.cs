using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace CsAgent.Core;

public sealed record IngestSummary(
    string CorpusName,
    string StorePath,
    int PagesLoaded,
    int PagesIngested,
    int PagesResumed,
    int ChunksEmbedded,
    IReadOnlyList<string> Skipped,
    double Seconds);

/// <summary>
/// Path or URL mode ingest: load → chunk → embed (batched) → store, page
/// by page. Idempotent: a page whose content hash is unchanged is skipped
/// without re-embedding, so a killed ingest resumes where it stopped.
/// </summary>
public sealed class IngestPipeline(
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    SqliteVectorStore store,
    int chunkSize,
    int chunkOverlap,
    int embedBatchSize = 64)
{
    public IngestSummary Run(string rootPath, string corpusName) =>
        Run(CorpusLoader.Load(rootPath), corpusName);

    /// <summary>URL-mode entry: crawl results (or any page stream) through the
    /// same chunk→embed→store loop. Key = absolute URL; hash covers the recipe,
    /// so an unchanged URL-page resumes without re-embedding.</summary>
    public IngestSummary Run(LoadReport report, string corpusName)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var pagesIngested = 0;
        var pagesResumed = 0;
        var chunksEmbedded = 0;
        var dimensionRecorded = true;

        foreach (var page in report.Pages)
        {
            // hash covers the chunking recipe, not just the text: a config or
            // prefix change must trigger re-embedding, never stale chunks.
            var hash = Sha256Hex($"{ChunkingVersion}|{chunkSize}|{chunkOverlap}|{page.Text}");
            if (!store.NeedsIngest(page.Key, hash))
            {
                pagesResumed++;
                continue;
            }

            var title = PageTitle(page);
            var chunks = Chunker.Chunk(page.Text, chunkSize, chunkOverlap)
                .Select(c => $"{title} — {c}")
                .ToList();
            var withEmbeddings = new List<(string Text, float[] Embedding)>(chunks.Count);
            foreach (var batch in Batch(chunks, embedBatchSize))
            {
                var vectors = embeddings.GenerateAsync(batch).GetAwaiter().GetResult();
                if (vectors.Count != batch.Count)
                    throw new CsAgentException(new CsAgentError(
                        "ingest", "embedding-count-mismatch",
                        $"Embedding provider returned {vectors.Count} vectors for {batch.Count} chunks on '{page.Key}'."));
                for (var i = 0; i < batch.Count; i++)
                    withEmbeddings.Add((batch[i], vectors[i].Vector.ToArray()));
                if (dimensionRecorded)
                {
                    store.RecordDimension(vectors[0].Vector.Length);
                    dimensionRecorded = false;
                }
            }

            store.UpsertPage(page.Key, hash, withEmbeddings);
            pagesIngested++;
            chunksEmbedded += withEmbeddings.Count;
        }

        stopwatch.Stop();
        return new IngestSummary(
            corpusName, "", report.Pages.Count, pagesIngested, pagesResumed,
            chunksEmbedded, report.Skipped, stopwatch.Elapsed.TotalSeconds);
    }

    private const string ChunkingVersion = "v2-title-prefix";

    /// <summary>First "# " heading text, else the file name (path mode) or the
    /// last URL segment / host (crawl mode).</summary>
    private static string PageTitle(LoadedPage page)
    {
        var line = page.Text.Split('\n').FirstOrDefault(l => l.StartsWith("# "));
        if (line is not null) return line[2..].Trim();
        if (Uri.TryCreate(page.Key, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var segment = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
            var withoutExt = Path.GetFileNameWithoutExtension(segment);
            return withoutExt.Length > 0 ? withoutExt : uri.Host;
        }
        return Path.GetFileNameWithoutExtension(page.Key);
    }

    private static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IEnumerable<List<T>> Batch<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
            yield return items.Skip(i).Take(size).ToList();
    }
}