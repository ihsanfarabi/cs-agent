using CsAgent.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// Local-only gate over the gitignored 2026-09-06 docusaurus repro store
/// (cs-agent-docusaurus-io.db, repo root, ~3,960 chunks). CI cannot see the
/// .db, so these facts SKIP there — honest scope documented in the design
/// doc (design-2026-09-08-mmr-diversity.md, D5).
/// </summary>
public sealed class ReproStoreFactAttribute : FactAttribute
{
    public const string DbFileName = "cs-agent-docusaurus-io.db";

    public ReproStoreFactAttribute()
    {
        if (ReproStore.FindDbPath() is null)
            Skip = $"{DbFileName} not found (gitignored local artifact) — runs only where the repro store exists";
    }
}

public static class ReproStore
{
    public const string DbFileName = ReproStoreFactAttribute.DbFileName;

    /// <summary>Walks up from the test assembly to the repo root looking for the repro .db.</summary>
    public static string? FindDbPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent!)
        {
            var candidate = Path.Combine(dir.FullName, DbFileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Opens the repro store through SqliteVectorStore, passing whatever embedding
    /// model the store itself records (read from store_meta first, so the test never
    /// guesses the model name). Note: the store constructor rewrites the same-value
    /// embedding_model meta row on open — benign, same value, no data change.
    /// </summary>
    public static SqliteVectorStore OpenStore()
    {
        var path = FindDbPath() ?? throw new InvalidOperationException(
            $"{DbFileName} not found — see {nameof(ReproStoreFactAttribute)}.");
        var model = ReadMeta(path, "embedding_model")
            ?? throw new InvalidOperationException($"{DbFileName} has no embedding_model meta row.");
        return new SqliteVectorStore(path, model);
    }

    /// <summary>Raw read-only read of every chunk vector — no store API, no writes.</summary>
    public static IReadOnlyList<(string PagePath, int Ordinal, float[] Vector)> ReadChunks(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT page_path, ordinal, embedding FROM chunks";
        using var reader = cmd.ExecuteReader();
        var chunks = new List<(string PagePath, int Ordinal, float[] Vector)>();
        while (reader.Read())
        {
            var blob = (byte[])reader[2];
            var vector = new float[blob.Length / sizeof(float)];
            Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
            chunks.Add((reader.GetString(0), reader.GetInt32(1), vector));
        }
        return chunks;
    }

    private static string? ReadMeta(string dbPath, string key)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM store_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}
