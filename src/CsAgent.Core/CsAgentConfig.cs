namespace CsAgent.Core;

/// <summary>
/// Env-var-only configuration (no config files). CS_AGENT_MODEL_KEY is the only
/// required var; everything else has a documented default. Numeric vars are
/// validated at startup — structured error naming the var and its valid range,
/// before any model call.
/// </summary>
public sealed record CsAgentConfig(
    string ModelKey,
    string BaseUrl,
    string DraftModel,
    string VerifyModel,
    string EmbeddingModel,
    string? EmbeddingDeployment,
    int TopK,
    int ChunkSize,
    int ChunkOverlap,
    string StorePath)
{
    /// <summary>The full CS_AGENT_* var set every surface must forward to the pipeline.</summary>
    public static readonly string[] EnvKeys =
    [
        "CS_AGENT_MODEL_KEY", "CS_AGENT_BASE_URL", "CS_AGENT_DRAFT_MODEL",
        "CS_AGENT_VERIFY_MODEL", "CS_AGENT_EMBEDDING_MODEL",
        "CS_AGENT_EMBEDDING_DEPLOYMENT", "CS_AGENT_TOP_K", "CS_AGENT_CHUNK_SIZE",
        "CS_AGENT_CHUNK_OVERLAP", "CS_AGENT_STORE",
    ];

    /// <summary>Reads the env vars from the process environment (MCP servers inherit them from the host).</summary>
    public static CsAgentConfig FromCurrentEnvironment(ISet<string> reservedCorpusNames) =>
        FromEnvironment(
            EnvKeys.ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k)),
            reservedCorpusNames);

    public static CsAgentConfig FromEnvironment(
        IReadOnlyDictionary<string, string?> env,
        ISet<string> reservedCorpusNames)
    {
        if (!env.TryGetValue("CS_AGENT_MODEL_KEY", out var key) || string.IsNullOrWhiteSpace(key))
            throw new CsAgentException(new CsAgentError(
                "config", "missing-required-env",
                "CS_AGENT_MODEL_KEY is required (provider API key). Set it and re-run."));

        var topK = ValidateInt(env, "CS_AGENT_TOP_K", 8, min: 1, max: 50);
        var chunkSize = ValidateInt(env, "CS_AGENT_CHUNK_SIZE", 1200, min: 200, max: 10_000);
        var overlap = ValidateInt(env, "CS_AGENT_CHUNK_OVERLAP", 150, min: 0, max: chunkSize - 1);
        var baseUrl = NonEmpty(env, "CS_AGENT_BASE_URL", "https://api.openai.com/v1");

        return new CsAgentConfig(
            ModelKey: key,
            BaseUrl: baseUrl,
            // defaults = the pair that passed the eval gate: DeepSeek draft + gpt-4o-mini gate.
            // DeepSeek is OpenRouter-only — the default BASE_URL must be openrouter for it to resolve.
            DraftModel: NonEmpty(env, "CS_AGENT_DRAFT_MODEL", "deepseek/deepseek-v4-flash-0731"),
            VerifyModel: NonEmpty(env, "CS_AGENT_VERIFY_MODEL", "gpt-4o-mini"),
            EmbeddingModel: NonEmpty(env, "CS_AGENT_EMBEDDING_MODEL", "text-embedding-3-small"),
            EmbeddingDeployment: env.TryGetValue("CS_AGENT_EMBEDDING_DEPLOYMENT", out var dep) && !string.IsNullOrWhiteSpace(dep) ? dep : null,
            TopK: topK,
            ChunkSize: chunkSize,
            ChunkOverlap: overlap,
            StorePath: NonEmpty(env, "CS_AGENT_STORE", ""));
    }

    private static string NonEmpty(
        IReadOnlyDictionary<string, string?> env, string name, string @default)
    {
        var raw = env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : @default;
        return raw ?? throw new CsAgentException(new CsAgentError(
            "config", "empty-env", $"{name} must not be empty."));
    }

    private static int ValidateInt(
        IReadOnlyDictionary<string, string?> env, string name,
        int @default, int min, int max)
    {
        var raw = env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        if (raw is null) return @default;
        if (!int.TryParse(raw, out var value) || value < min || value > max)
            throw new CsAgentException(new CsAgentError(
                "config", "invalid-env-value",
                $"{name} must be an integer in [{min}, {max}]; got \"{raw}\"."));
        return value;
    }
}