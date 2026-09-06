using Microsoft.Data.Sqlite;

namespace CsAgent.Core;

public sealed record ChunkHit(string PagePath, int Ordinal, string Text, float Score);

/// <summary>
/// SQLite file-backed vector store: embeddings as BLOBs, cosine in-process,
/// one .db per corpus. Page-by-page upsert = idempotent re-ingest.
/// Corrupt .db and embedding-model mismatch are named structured errors.
/// </summary>
public sealed class SqliteVectorStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteVectorStore(string path, string embeddingModel)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new CsAgentException(new CsAgentError(
                "store", "missing-store-path", "Store path must not be empty."));

        try
        {
            _connection = new SqliteConnection($"Data Source={path}");
            _connection.Open();
            EnsureSchema();
            RequireCompatibleModel(embeddingModel);
            SetMeta("embedding_model", embeddingModel);
        }
        catch (SqliteException)
        {
            _connection?.Dispose();
            throw new CsAgentException(new CsAgentError(
                "store", "corrupt-store",
                $"Could not open SQLite store at '{path}'. Delete the .db file and re-ingest."));
        }
    }

    /// <summary>True when the page is missing or its content hash changed.</summary>
    public bool NeedsIngest(string pagePath, string contentHash)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT content_hash FROM pages WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", pagePath);
        var stored = cmd.ExecuteScalar() as string;
        return stored is null || stored != contentHash;
    }

    public void UpsertPage(string pagePath, string contentHash, IReadOnlyList<(string Text, float[] Embedding)> chunks)
    {
        using var tx = _connection.BeginTransaction();
        try
        {
            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM chunks WHERE page_path = $p; DELETE FROM pages WHERE path = $p;";
                del.Parameters.AddWithValue("$p", pagePath);
                del.ExecuteNonQuery();
            }

            using (var page = _connection.CreateCommand())
            {
                page.Transaction = tx;
                page.CommandText = "INSERT INTO pages(path, ingested_at, content_hash) VALUES($p, $t, $h);";
                page.Parameters.AddWithValue("$p", pagePath);
                page.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                page.Parameters.AddWithValue("$h", contentHash);
                page.ExecuteNonQuery();
            }

            for (var i = 0; i < chunks.Count; i++)
            {
                var (text, embedding) = chunks[i];
                using var chunk = _connection.CreateCommand();
                chunk.Transaction = tx;
                chunk.CommandText =
                    "INSERT INTO chunks(page_path, ordinal, text, embedding) VALUES($p, $o, $t, $e);";
                chunk.Parameters.AddWithValue("$p", pagePath);
                chunk.Parameters.AddWithValue("$o", i);
                chunk.Parameters.AddWithValue("$t", text);
                chunk.Parameters.AddWithValue("$e", ToBlob(embedding));
                chunk.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (SqliteException)
        {
            tx.Rollback();
            throw new CsAgentException(new CsAgentError(
                "store", "write-failed",
                $"Failed to write page '{pagePath}' to the store."));
        }
    }

    /// <summary>Top-k chunks by cosine against the query vector.</summary>
    public IReadOnlyList<ChunkHit> TopK(ReadOnlySpan<float> query, int k)
    {
        if (!HasDocuments)
            throw new CsAgentException(new CsAgentError(
                "store", "empty-store",
                "No documents ingested. Run `cs-agent ingest <path>` first."));

        var dim = query.Length;
        var results = new List<(ChunkHit Hit, float[] Vector)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT page_path, ordinal, text, embedding FROM chunks";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var vector = FromBlob((byte[])reader[3], dim);
                results.Add((
                    new ChunkHit(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                        VectorMath.Cosine(query, vector)),
                    vector));
            }
        }
        return results
            .OrderByDescending(r => r.Hit.Score)
            .Take(k)
            .Select(r => r.Hit)
            .ToList();
    }

    public bool HasDocuments
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM chunks)";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
    }

    public string? StoredEmbeddingModel
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM store_meta WHERE key = 'embedding_model'";
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Records the embedding dimension observed from the first real embedding.</summary>
    public void RecordDimension(int dimension)
    {
        if (dimension <= 0)
            throw new CsAgentException(new CsAgentError(
                "store", "invalid-dimension", $"Embedding dimension must be > 0; got {dimension}."));
        SetMeta("vector_dimension", dimension.ToString());
    }

    public void Dispose() => _connection.Dispose();

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pages(
                path TEXT PRIMARY KEY,
                ingested_at TEXT NOT NULL,
                content_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chunks(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                page_path TEXT NOT NULL REFERENCES pages(path) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                text TEXT NOT NULL,
                embedding BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS store_meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            PRAGMA foreign_keys = ON;
            """;
        cmd.ExecuteNonQuery();
    }

    private void RequireCompatibleModel(string embeddingModel)
    {
        var stored = StoredEmbeddingModel;
        if (stored is not null && stored != embeddingModel)
            throw new CsAgentException(new CsAgentError(
                "store", "embedding-model-mismatch",
                $"Store was embedded with '{stored}' but CS_AGENT_EMBEDDING_MODEL is '{embeddingModel}'. " +
                "Cosine across mismatched vector spaces returns confident nonsense — delete the .db and re-ingest."));
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO store_meta(key, value) VALUES($k, $v);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] blob, int expectedDim)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, vector.Length * sizeof(float));
        if (vector.Length != expectedDim)
            throw new CsAgentException(new CsAgentError(
                "store", "dimension-mismatch",
                $"Stored vector has {vector.Length} dims; query has {expectedDim}."));
        return vector;
    }
}