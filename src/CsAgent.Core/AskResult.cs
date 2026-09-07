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
/// The optional audit trace for one completed ask. Retrieved chunks and
/// per-claim support already live in AskResult (citations/claims); this adds
/// what no surface prints: the draft as written — even when the verifier
/// rejected it — and the raw verifier JSON. Never fabricated: a transport
/// failure leaves verifier_raw null.
/// </summary>
public sealed record AskEvidence(
    [property: JsonPropertyName("draft_answer")] string DraftAnswer,
    [property: JsonPropertyName("draft_rejected")] bool DraftRejected,
    [property: JsonPropertyName("verifier_raw")] string? VerifierRaw);

/// <summary>One completed ask: the canonical result plus its evidence trace.</summary>
public sealed record AskRun(AskResult Result, AskEvidence Evidence);

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
    public AskResult Run(string question, CancellationToken cancellationToken = default)
        => RunWithEvidence(question, cancellationToken).Result;

    /// <summary>
    /// Run returning the canonical result plus the evidence trace. AskResult
    /// alone stays the contract every surface renders — Run keeps that shape
    /// for CLI/MCP/eval; only the HTTP evidence surface reads this method.
    /// </summary>
    public AskRun RunWithEvidence(string question, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("retrieving");
        var chunks = retriever.Retrieve(question, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("drafting");
        var draft = draftAgentFactory();
        var draftResponse = draft.RunAsync(
            FormatDraftPrompt(question, chunks), cancellationToken: cancellationToken).GetAwaiter().GetResult();
        var answer = draftResponse.Text.Trim();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("verifying");
        var (claims, verifierRaw) = VerifyOnce(question, answer, chunks, cancellationToken);

        stopwatch.Stop();

        AskResult result;
        if (claims is null)
            // second malformed verdict ⇒ fail closed to escalate
            result = new AskResult(question, false, null,
                [new MissingItem(question, "verifier output malformed twice — failing closed")],
                [], chunks, Calls: 3, stopwatch.Elapsed.TotalSeconds, CostLine());
        else if (VerifierRule.Resolve(claims))
            result = new AskResult(question, true, answer, null, claims, chunks, Calls: 2,
                stopwatch.Elapsed.TotalSeconds, CostLine());
        else
            result = new AskResult(question, false, null, VerifierRule.BuildMissing(claims, question),
                claims, chunks, Calls: 2, stopwatch.Elapsed.TotalSeconds, CostLine());

        return new AskRun(result, new AskEvidence(answer, !result.Resolved, verifierRaw));
    }

    private (IReadOnlyList<ClaimVerdict>? Claims, string? Raw) VerifyOnce(
        string question, string answer, IReadOnlyList<CitedChunk> chunks, CancellationToken cancellationToken)
    {
        var verify = verifyAgentFactory();
        var prompt = FormatVerifyPrompt(question, answer, chunks);
        string? raw = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = verify.RunAsync<IReadOnlyList<ClaimVerdict>>(
                    prompt, cancellationToken: cancellationToken).GetAwaiter().GetResult();
                raw = response.Text; // captured BEFORE the Result check — malformed text IS the evidence
                if (response.Result is not null)
                    return (response.Result, raw);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                // malformed structured output — the pipeline's ONLY retry.
                // Cancellation is exempt: shutdown propagates instead of
                // fabricating a fail-closed verdict (verify stays fail-closed
                // for every non-cancellation failure — unchanged).
            }
            prompt = prompt + "\n\nYour previous output was not valid JSON. Output ONLY the JSON array.";
        }
        return (null, raw); // caller treats null claims as fail-closed to escalate
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
        AppendChunks(sb, chunks);
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
        AppendChunks(sb, chunks);
        return sb.ToString();
    }

    /// <summary>
    /// Delimiter isolates corpus data from prompt instructions; a chunk that
    /// literally contains "END CHUNK>>>" could still break delimiting —
    /// residual risk documented in the README trust-boundary statement.
    /// </summary>
    private static void AppendChunks(System.Text.StringBuilder sb, IReadOnlyList<CitedChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            sb.AppendLine($"<<<CHUNK {chunk.Number} ({chunk.PagePath})");
            sb.AppendLine(chunk.Text);
            sb.AppendLine("END CHUNK>>>");
        }
    }

    /// <summary>
    /// Static price table cannot price BYO models, and no per-call token usage
    /// is captured, so v1 always prints the non-default marker (documented
    /// limitation #1). Defaults are deepseek/deepseek-v4-flash-0731 in both
    /// roles; a real cost line needs usage capture first.
    /// </summary>
    private static string CostLine() => "— (non-default model)";
}