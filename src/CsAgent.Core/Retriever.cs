using Microsoft.Extensions.AI;

namespace CsAgent.Core;

public sealed record CitedChunk(
    [property: System.Text.Json.Serialization.JsonPropertyName("number")] int Number,
    [property: System.Text.Json.Serialization.JsonPropertyName("page_path")] string PagePath,
    [property: System.Text.Json.Serialization.JsonPropertyName("ordinal")] int Ordinal,
    [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("score")] float Score);

/// <summary>Query embed + top-k from the store; chunks numbered [1..k] for citation.</summary>
public sealed class Retriever(IEmbeddingGenerator<string, Embedding<float>> embeddings, SqliteVectorStore store, int topK)
{
    public IReadOnlyList<CitedChunk> Retrieve(string question)
    {
        var vector = embeddings.GenerateVectorAsync(question).GetAwaiter().GetResult();
        var hits = store.TopK(vector.Span, topK);
        return [.. hits.Select((h, i) => new CitedChunk(i + 1, h.PagePath, h.Ordinal, h.Text, h.Score))];
    }
}