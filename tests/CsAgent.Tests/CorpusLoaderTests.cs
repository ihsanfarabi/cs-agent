using CsAgent.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

/// <summary>Deterministic fake embedding generator (SHA-based, 8 dims) — no network.</summary>
sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
            embeddings.Add(new Embedding<float>(HashToVector(value)));
        return Task.FromResult(embeddings);
    }

    internal static float[] HashToVector(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        var vector = new float[8];
        for (var i = 0; i < 8; i++)
            vector[i] = bytes[i] / 255f - 0.5f;
        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}


public sealed class CorpusLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cs-agent-load-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string FileRel(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void LoadsMdAndHtml_WithRelativePaths()
    {
        FileRel("docs/api-keys.md", "# Keys\nrotate");
        FileRel("docs/index.html", "<html><body><h1>Home</h1><p>Hi</p></body></html>");
        File.WriteAllBytes(Path.Combine(_root, "docs/bad.md"), new byte[] { 0x00, 0x01, 0x02 });

        var report = CorpusLoader.Load(_root);
        Assert.Equal(2, report.Pages.Count);
        Assert.Contains(report.Pages, p => p.RelativePath == "docs/api-keys.md");
        Assert.Contains(report.Pages, p => p.RelativePath == "docs/index.html");
        Assert.Contains(report.Skipped, s => s.Contains("bad.md"));
    }

    [Fact]
    public void HtmlIsStrippedToText()
    {
        FileRel("a.html", "<html><style>.x{}</style><body><h1>Title</h1><p>Body&nbsp;text</p></body></html>");
        var report = CorpusLoader.Load(_root);
        var page = Assert.Single(report.Pages);
        Assert.Contains("Title", page.Text);
        Assert.Contains("Body text", page.Text);
        Assert.DoesNotContain("<h1>", page.Text);
        Assert.DoesNotContain(".x{}", page.Text);
    }

    [Fact]
    public void NonexistentPath_ThrowsNamedError()
    {
        var ex = Assert.Throws<CsAgentException>(() => CorpusLoader.Load(Path.Combine(_root, "nope")));
        Assert.Equal("path-not-found", ex.Error.Code);
    }

    [Fact]
    public void FileInsteadOfDirectory_ThrowsNamedError()
    {
        var file = FileRel("single.md", "x");
        var ex = Assert.Throws<CsAgentException>(() => CorpusLoader.Load(file));
        Assert.Equal("path-not-directory", ex.Error.Code);
    }

    [Fact]
    public void SymlinkCycle_Guarded()
    {
        FileRel("a.md", "real");
        var sub = Path.Combine(_root, "loop");
        Directory.CreateDirectory(sub);
        Directory.CreateSymbolicLink(Path.Combine(sub, "back"), _root);

        var report = CorpusLoader.Load(_root); // must terminate
        Assert.Single(report.Pages);
    }
}

public sealed class IngestPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cs-agent-ing-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-ing-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private IngestPipeline MakePipeline()
    {
        var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        return new IngestPipeline(
            new FakeEmbeddingGenerator(),
            store, chunkSize: 1200, chunkOverlap: 150, embedBatchSize: 8);
    }

    [Fact]
    public void IngestAndResume_CountsCorrectly()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.md"), "# A\n" + new string('a', 2600)); // 3 chunks
        File.WriteAllText(Path.Combine(_root, "b.md"), "# B\nshort");

        var summary = MakePipeline().Run(_root, "corpus");
        Assert.Equal(2, summary.PagesLoaded);
        Assert.Equal(2, summary.PagesIngested);
        Assert.Equal(0, summary.PagesResumed);
        Assert.Equal(4, summary.ChunksEmbedded);

        var resumed = MakePipeline().Run(_root, "corpus");
        Assert.Equal(2, resumed.PagesResumed);
        Assert.Equal(0, resumed.PagesIngested);
        Assert.Equal(0, resumed.ChunksEmbedded);
    }

    [Fact]
    public void ChangedFile_Reingested_OtherResumed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.md"), "v1");
        File.WriteAllText(Path.Combine(_root, "b.md"), "static");
        MakePipeline().Run(_root, "corpus");

        File.WriteAllText(Path.Combine(_root, "a.md"), "v2 changed content");
        var summary = MakePipeline().Run(_root, "corpus");
        Assert.Equal(1, summary.PagesIngested);
        Assert.Equal(1, summary.PagesResumed);
    }
}