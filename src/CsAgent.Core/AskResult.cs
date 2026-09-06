using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CsAgent.Core;

/// <summary>
/// The ONE result object — CLI renders it (plus --json), MCP exposes fields,
/// DevUI displays. No surface builds its own copy. Field names are the
/// canonical JSON contract, pinned regardless of serializer options.
/// </summary>
public sealed record AskResult(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("resolved")] bool Resolved,
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("missing")] IReadOnlyList<MissingItem>? Missing,
    [property: JsonPropertyName("claims")] IReadOnlyList<ClaimVerdict> Claims,
    [property: JsonPropertyName("citations")] IReadOnlyList<CitedChunk> CitedChunks,
    [property: JsonPropertyName("calls")] int Calls,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("estimated_cost")] string? CostEstimate);

/// <summary>
/// The ask pipeline: retrieve → draft → verify (ONE call) → verdict BEFORE any
/// output. Verdict exists before this returns; no streaming in v1.
/// </summary>
public sealed class AskPipeline(
    Func<AIAgent> draftAgentFactory,
    Func<AIAgent> verifyAgentFactory,
    Retriever retriever,
    Action<string>? progress = null)
{
    public AskResult Run(string question)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        progress?.Invoke("retrieving");
        var chunks = retriever.Retrieve(question);

        progress?.Invoke("drafting");
        var draft = draftAgentFactory();
        var draftResponse = draft.RunAsync(FormatDraftPrompt(question, chunks)).GetAwaiter().GetResult();
        var answer = draftResponse.Text.Trim();

        progress?.Invoke("verifying");
        var claims = VerifyOnce(question, answer, chunks);

        stopwatch.Stop();

        if (claims is null)
            // second malformed verdict ⇒ fail closed to escalate
            return new AskResult(question, false, null,
                [new MissingItem(question, "verifier output malformed twice — failing closed")],
                [], chunks, Calls: 3, stopwatch.Elapsed.TotalSeconds, CostLine());

        var resolved = VerifierRule.Resolve(claims);
        return resolved
            ? new AskResult(question, true, answer, null, claims, chunks, Calls: 2,
                stopwatch.Elapsed.TotalSeconds, CostLine())
            : new AskResult(question, false, null, VerifierRule.BuildMissing(claims, question),
                claims, chunks, Calls: 2, stopwatch.Elapsed.TotalSeconds, CostLine());
    }

    private IReadOnlyList<ClaimVerdict>? VerifyOnce(
        string question, string answer, IReadOnlyList<CitedChunk> chunks)
    {
        var verify = verifyAgentFactory();
        var prompt = FormatVerifyPrompt(question, answer, chunks);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = verify.RunAsync<IReadOnlyList<ClaimVerdict>>(prompt).GetAwaiter().GetResult();
                if (response.Result is not null)
                    return response.Result;
            }
            catch (Exception)
            {
                // malformed structured output — the pipeline's ONLY retry
            }
            prompt = prompt + "\n\nYour previous output was not valid JSON. Output ONLY the JSON array.";
        }
        return null; // caller treats null as fail-closed to escalate
    }

    /// <summary>
    /// Agents are built with temperature 0 by their factory (ChatClientAgentOptions
    /// .ChatOptions) — the gate must not flip on sampling variance.
    /// </summary>
    internal static string FormatDraftPrompt(string question, IReadOnlyList<CitedChunk> chunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"QUESTION: {question}");
        sb.AppendLine("CHUNKS:");
        foreach (var chunk in chunks)
            sb.AppendLine($"[{chunk.Number}] ({chunk.PagePath}) {chunk.Text}");
        sb.AppendLine("Answer the question using only these chunks, citing [n] after each claim.");
        return sb.ToString();
    }

    internal static string FormatVerifyPrompt(
        string question, string answer, IReadOnlyList<CitedChunk> chunks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Prompts.VerifyPrompt());
        sb.AppendLine($"QUESTION: {question}");
        sb.AppendLine($"DRAFT ANSWER: {answer}");
        sb.AppendLine("CHUNKS:");
        foreach (var chunk in chunks)
            sb.AppendLine($"[{chunk.Number}] ({chunk.PagePath}) {chunk.Text}");
        return sb.ToString();
    }

    /// <summary>
    /// Static price table cannot price BYO models, and no per-call token usage
    /// is captured, so v1 always prints the non-default marker (documented
    /// limitation #1). Defaults are deepseek/deepseek-v4-flash-0731 +
    /// gpt-4o-mini; a real cost line needs usage capture first.
    /// </summary>
    private static string CostLine() => "— (non-default model)";
}