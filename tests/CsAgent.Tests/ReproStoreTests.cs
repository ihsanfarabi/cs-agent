using System.Text.RegularExpressions;
using CsAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace CsAgent.Tests;

/// <summary>
/// D5.1 measurement gate (design-2026-09-08-mmr-diversity.md): before MMR lands,
/// prove the real repro store has the similarity gap λ=0.7 exploits — clone twins
/// (same page content under versioned URLs) must sit clearly above adjacent
/// same-page chunks. RED = STOP: bring the printed numbers to the user and
/// re-decide λ. Never implement past a red gate here.
/// </summary>
public sealed class ReproStoreTests
{
    private readonly ITestOutputHelper _output;
    public ReproStoreTests(ITestOutputHelper output) => _output = output;

    /// <summary>Strips version segments so /docs/3.9.2/installation,
    /// /docs/installation and /docs/next/installation map to one family key.</summary>
    internal static string CloneFamilyKey(string pagePath)
    {
        var segments = pagePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "next" && !Regex.IsMatch(s, @"^v?\d+(\.\d+)+$"));
        return string.Join("/", segments);
    }

    [ReproStoreFact]
    public void CloneTwins_ClearlyMoreSimilarThanAdjacentChunks()
    {
        var chunks = ReproStore.ReadChunks(ReproStore.FindDbPath()!);
        _output.WriteLine($"{chunks.Count} chunks in repro store");

        // Cross-page pairs inside a clone family = versioned-URL clone twins.
        // Ordinal-ALIGNED only: chunk i of one version vs chunk i of another is
        // the same content. Cross-ordinal pairs are different content and drag
        // the median to the noise floor (~0.57 measured) — do not compare them.
        var cloneSims = new List<float>();
        foreach (var family in chunks.GroupBy(c => CloneFamilyKey(c.PagePath)))
        {
            var pages = family.GroupBy(c => c.PagePath).ToList();
            if (pages.Count < 2) continue;
            foreach (var pageA in pages)
                foreach (var pageB in pages.Where(p => !ReferenceEquals(p, pageA)))
                    foreach (var a in pageA)
                        foreach (var b in pageB.Where(b => b.Ordinal == a.Ordinal))
                            cloneSims.Add(VectorMath.Cosine(a.Vector, b.Vector));
        }

        // Same-page adjacent ordinals share the 150-char chunk overlap.
        var adjacentSims = new List<float>();
        foreach (var page in chunks.GroupBy(c => c.PagePath))
        {
            var byOrdinal = page.OrderBy(c => c.Ordinal).ToList();
            for (var i = 1; i < byOrdinal.Count; i++)
                adjacentSims.Add(VectorMath.Cosine(byOrdinal[i - 1].Vector, byOrdinal[i].Vector));
        }

        Assert.NotEmpty(cloneSims);     // no clone families = wrong repro data — STOP
        Assert.NotEmpty(adjacentSims);

        cloneSims.Sort();
        adjacentSims.Sort();
        float Median(List<float> xs) => xs[xs.Count / 2];

        var cloneMedian = Median(cloneSims);
        var adjacentMedian = Median(adjacentSims);
        _output.WriteLine(
            $"clone-twin sims: n={cloneSims.Count}, median={cloneMedian:F4}, min={cloneSims[0]:F4}");
        _output.WriteLine(
            $"adjacent-chunk sims: n={adjacentSims.Count}, median={adjacentMedian:F4}, max={adjacentSims[^1]:F4}");

        // STOP gate (spec D5.1). Break-even: an MMR pick flips when
        // 0.3·(clone_sim − diverse_sim) > 0.7·(relevance_gap), so λ=0.7 needs
        // clones clearly above everything else a query can retrieve.
        Assert.True(cloneMedian >= 0.9f,
            $"clone-twin median {cloneMedian:F4} < 0.9 — versioned URLs are not near-clones. STOP (spec D5.1).");
        Assert.True(cloneMedian - adjacentMedian >= 0.05f,
            $"gap {cloneMedian - adjacentMedian:F4} < 0.05 — λ=0.7 cannot separate clones from adjacent chunks. STOP (spec D5.1).");
    }
}