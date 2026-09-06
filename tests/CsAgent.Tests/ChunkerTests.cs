using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public class ChunkerTests
{
    [Fact]
    public void HeadingBoundaries_NeverMerged()
    {
        var md = "# Alpha\n" + new string('a', 1500) + "\n# Beta\nshort text";
        var chunks = Chunker.Chunk(md, chunkSize: 1200, overlap: 150);
        Assert.Contains(chunks, c => c.StartsWith("# Beta"));
        Assert.All(chunks, c => Assert.True(c.Length <= 1200));
        // no chunk spans the boundary between Alpha and Beta
        Assert.DoesNotContain(chunks, c => c.Contains("# Beta") && c.Contains("# Alpha"));
    }

    [Fact]
    public void SmallSection_SingleChunk()
    {
        var chunks = Chunker.Chunk("# H\nhello", 1200, 150);
        var chunk = Assert.Single(chunks);
        Assert.Equal("# H\nhello", chunk);
    }

    [Fact]
    public void LongSection_WindowsOverlap()
    {
        var text = new string('x', 3000);
        var chunks = Chunker.Chunk(text, 1200, 150);
        Assert.Equal(3, chunks.Count);
        // overlap: chunk2 starts at chunk1 end - 150
        Assert.Equal(chunks[0][^150..], chunks[1][..150]);
        Assert.Equal(chunks[1][^150..], chunks[2][..150]);
    }

    [Fact]
    public void EmptyInput_NoChunks()
    {
        Assert.Empty(Chunker.Chunk("", 1200, 150));
        Assert.Empty(Chunker.Chunk("   \n  ", 1200, 150));
    }

    [Fact]
    public void InvalidSizes_ThrowNamedErrors()
    {
        var small = Assert.Throws<CsAgentException>(() => Chunker.Chunk("x", 100, 50));
        Assert.Equal("invalid-chunk-size", small.Error.Code);
        var bigOverlap = Assert.Throws<CsAgentException>(() => Chunker.Chunk("x", 300, 300));
        Assert.Equal("invalid-overlap", bigOverlap.Error.Code);
    }
}

public class VectorMathTests
{
    [Fact]
    public void KnownVectors_KnownRanking()
    {
        var q = new float[] { 1, 0, 0 };
        var a = new float[] { 1, 0.1f, 0 };
        var b = new float[] { 0, 1, 0 };
        Assert.True(VectorMath.Cosine(q, a) > VectorMath.Cosine(q, b));
        Assert.Equal(1f, VectorMath.Cosine(q, q), 4);
    }

    [Fact]
    public void ZeroVector_ScoresZero()
    {
        Assert.Equal(0f, VectorMath.Cosine(new float[3], new[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void DimensionMismatch_ThrowsNamedError()
    {
        var ex = Assert.Throws<CsAgentException>(() =>
            VectorMath.Cosine(new float[3], new float[4]));
        Assert.Equal("dimension-mismatch", ex.Error.Code);
    }
}