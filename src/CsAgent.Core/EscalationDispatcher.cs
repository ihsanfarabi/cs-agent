using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CsAgent.Core;

/// <summary>The failed ask the escalation agent composes a ticket from.</summary>
public sealed record EscalationContext(
    string Question,
    IReadOnlyList<MissingItem> Missing,
    IReadOnlyList<CitedChunk> Citations,
    string DraftAnswer);

/// <summary>
/// The escalation action: when a verdict comes back escalate and the webhook is
/// configured, a MAF agent with ONE tool (file_ticket) composes a ticket and
/// POSTs it to the webhook. Best-effort and recorded (D3): TryDispatch never
/// throws and never touches the verdict — the outcome rides the result object
/// as escalation_action. One attempt, no retry.
/// </summary>
public sealed class EscalationDispatcher(
    Func<EscalationContext, AITool, AIAgent> escalationAgentFactory,
    Uri webhook,
    HttpMessageHandler? handler = null,
    TimeSpan? postTimeout = null,
    TimeSpan? modelCallTimeout = null) : IDisposable
{
    private readonly TimeSpan _postTimeout = postTimeout ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _modelCallTimeout = modelCallTimeout ?? ModelCallTimeout.Default;
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler())
    {
        Timeout = postTimeout ?? TimeSpan.FromSeconds(30),
    };

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Runs the escalation agent once. Never throws; never touches the verdict.
    /// Null return = cancelled (nothing happened, no fabricated failed record).
    /// Any non-cancellation failure — agent timeout, agent never calling the
    /// tool, non-2xx, POST timeout — is a status:"failed" record.
    /// </summary>
    public EscalationActionRecord? TryDispatch(EscalationContext context, CancellationToken cancellationToken = default)
    {
        var attempt = new TicketAttempt();
        // Per-dispatch tool closure (concurrency-safe — no shared mutable state):
        // the tool is built fresh for each dispatch, capturing only this context.
        var tool = AIFunctionFactory.Create(
            (string title, string body, CancellationToken ct) => FileTicket(attempt, context, title, body, ct),
            "file_ticket",
            "File the escalation ticket for a question the ingested docs could not answer.");
        string? agentFailure = null;
        try
        {
            var agent = escalationAgentFactory(context, tool);
            ModelCallTimeout.Await("escalation",
                ct => agent.RunAsync(FormatPrompt(context), cancellationToken: ct),
                _modelCallTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null; // shutdown: cancellation propagates as a null record
        }
        catch (CsAgentException ex) when (ex.Error.Code == "call-timeout")
        {
            agentFailure = ex.Error.Message; // stalled route — a failed record, not a wedge
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            agentFailure = $"escalation agent run failed: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            return null; // cancellation out of the agent stack — never a fabricated failure
        }
        return attempt.ToRecord(agentFailure);
    }

    /// <summary>
    /// The tool body: POSTs the ticket (code-enriched payload — title/body from
    /// the agent's args, context from the captured EscalationContext) and
    /// returns a receipt string to the agent loop. One attempt: a repeat tool
    /// call never re-POSTs.
    /// </summary>
    private string FileTicket(
        TicketAttempt attempt, EscalationContext context, string title, string body, CancellationToken ct)
    {
        attempt.ToolCalled = true;
        attempt.Title ??= title;
        if (attempt.Posted) // one attempt, no retry — the same failure is re-reported
            return attempt.Failure is null ? $"HTTP {attempt.StatusCode}" : $"failed: {attempt.Failure}";
        attempt.Posted = true;
        try
        {
            var code = PostTicket(context, title, body, ct);
            attempt.StatusCode = code;
            return code >= 200 && code < 300
                ? $"HTTP {code}"
                : $"failed: HTTP {code}";
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            attempt.Failure = $"POST to webhook timed out after {_postTimeout.TotalSeconds:0.#}s";
            return $"failed: {attempt.Failure}";
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation propagates — never a fabricated record
        }
        catch (Exception ex)
        {
            attempt.Failure = $"POST to webhook failed: {ex.Message}";
            return $"failed: {attempt.Failure}";
        }
    }

    private int PostTicket(EscalationContext context, string title, string body, CancellationToken ct)
    {
        var payload = new
        {
            tool = "cs-agent-escalation",
            question = context.Question,
            title,
            body,
            missing = context.Missing.Select(m => m.Claim).ToArray(),
            citations = context.Citations.Select(c => c.PagePath).Distinct().ToArray(),
            timestamp = DateTime.UtcNow.ToString("o"),
        };
        using var content = new StringContent(
            JsonSerializer.Serialize(payload, CsAgentJson.SerializerOptions), Encoding.UTF8, "application/json");
        using var response = _http.PostAsync(webhook, content, ct).GetAwaiter().GetResult();
        return (int)response.StatusCode;
    }

    /// <summary>
    /// The run prompt: the four filled context sections of the pinned escalation
    /// template (the template itself rides the agent as instructions).
    /// </summary>
    internal static string FormatPrompt(EscalationContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"QUESTION: {context.Question}");
        sb.AppendLine("MISSING:");
        foreach (var m in context.Missing)
            sb.AppendLine($"- {m.Claim}");
        if (context.Missing.Count == 0)
            sb.AppendLine("- (nothing named)");
        sb.AppendLine("NEAREST PAGES:");
        var pages = context.Citations.Select(c => c.PagePath).Distinct().ToList();
        foreach (var page in pages)
            sb.AppendLine($"- {page}");
        if (pages.Count == 0)
            sb.AppendLine("- (none)");
        sb.AppendLine($"REJECTED DRAFT (context only, do not re-answer): {context.DraftAnswer}");
        return sb.ToString();
    }

    /// <summary>One dispatch's mutable outcome state — fresh per TryDispatch call.</summary>
    private sealed class TicketAttempt
    {
        public bool ToolCalled;
        public bool Posted;
        public string? Title;
        public int? StatusCode; // the HTTP code when the POST completed
        public string? Failure;  // POST-level failure reason

        /// <summary>POST outcome dominates once the tool ran — a filed ticket is filed.</summary>
        public EscalationActionRecord? ToRecord(string? agentFailure)
        {
            if (!ToolCalled)
                return new EscalationActionRecord("file_ticket", "failed", null,
                    agentFailure ?? "escalation agent did not call file_ticket");
            if (StatusCode is int code && code >= 200 && code < 300)
                return new EscalationActionRecord("file_ticket", "sent", Title, $"HTTP {code}");
            if (StatusCode is int failedCode)
                return new EscalationActionRecord("file_ticket", "failed", Title, $"HTTP {failedCode}");
            return new EscalationActionRecord("file_ticket", "failed", Title,
                Failure ?? agentFailure ?? "escalation agent run failed");
        }
    }
}