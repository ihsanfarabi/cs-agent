namespace CsAgent.Core;

/// <summary>
/// Per model-call ceiling shared by every provider call: draft, verify, the
/// query embed in Retriever, the batch embed in IngestPipeline. Observed
/// live: an OpenRouter route accepted the verify request, returned 200 +
/// headers, and never streamed the body — the in-flight call held the serve
/// job gate for 20+ minutes with no framework default firing (ClientModel's
/// shared HttpClient disables the HttpClient timeout; its own per-message
/// timeout did not surface). A stalled call must become a loud error, never a
/// wedge. Overridable only for tests.
/// </summary>
internal static class ModelCallTimeout
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(240);

    /// <summary>
    /// Bounds ONE model call. The linked token aborts the underlying HTTP call
    /// where the provider stack honors it; the race bounds it even where it
    /// does not (a stalled call is abandoned, never awaited — the gate must be
    /// released regardless). Shutdown cancellation propagates unchanged.
    /// </summary>
    public static T Await<T>(string stage, Func<CancellationToken, Task<T>> call,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var task = call(cts.Token);
        if (!task.IsCompleted && Task.WaitAny(task, Task.Delay(timeout, cts.Token)) != 0)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw TimeoutError(stage);
        }
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            // the call honored the linked token and cancelled itself — same
            // timeout, reported through the same structured error
            throw TimeoutError(stage);
        }

        CsAgentException TimeoutError(string s) => new(new CsAgentError("model", "call-timeout",
            $"{s} model call did not complete within {timeout.TotalSeconds:0}s — provider stalled or unreachable."));
    }
}