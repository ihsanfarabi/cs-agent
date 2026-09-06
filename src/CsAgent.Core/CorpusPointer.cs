namespace CsAgent.Core;

/// <summary>
/// Most-recently-ingested corpus pointer: one small state file beside the .db
/// files. A corrupt pointer is auto-rebuilt from the existing .db files — never
/// a silent wrong-corpus answer.
/// </summary>
public static class CorpusPointer
{
    public static string PointerPath(string storePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storePath)) ?? ".", ".cs-agent-recency");

    public static void Update(string storePath)
    {
        File.WriteAllText(PointerPath(storePath), Path.GetFullPath(storePath) + "\n");
    }

    /// <summary>
    /// Resolve the store for `ask`: CS_AGENT_STORE if set, else the recency
    /// pointer, else a named error. A corrupt pointer is rebuilt from the .db
    /// files in the pointer's directory.
    /// </summary>
    public static string ResolveStorePath(string? envStore)
    {
        if (!string.IsNullOrWhiteSpace(envStore)) return envStore;
        var pointer = PointerPath("./cs-agent.db");
        if (File.Exists(pointer))
        {
            var text = File.ReadAllText(pointer).Trim();
            if (File.Exists(text)) return text;
            var rebuilt = Directory
                .EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(pointer)) ?? ".", "*.db")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (rebuilt is not null)
            {
                Update(rebuilt);
                return rebuilt;
            }
        }
        throw new CsAgentException(new CsAgentError(
            "ask", "no-corpus-selected",
            "No corpus selected. Run `cs-agent ingest <path>` first, or set CS_AGENT_STORE."));
    }
}