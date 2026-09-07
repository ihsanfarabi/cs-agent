using CsAgent.Core;
using Microsoft.AspNetCore.Mvc;
using System.ClientModel;

namespace CsAgent.Http;

/// <summary>
/// Named-error → ProblemDetails registry (RFC 9457, type URNs
/// `urn:cs-agent:&lt;component&gt;:&lt;name&gt;`). Status taxonomy: store errors
/// (empty/corrupt/embedding-model-mismatch) → 503; an exception that IS or
/// WRAPS (at any InnerException depth) HttpRequestException / IOException /
/// ClientResultException → 502 model-transport — the ONE type-based rule.
/// ClientResultException is pinned from the dead-BaseUrl live check
/// (eng-review D4): the OpenAI/ClientModel retry path surfaces transport
/// failures as a bare ClientResultException with NO inner exception, so a
/// bare exact-type match on HttpRequestException alone drops the 502 signal.
/// Any other named error → 500 carrying component+name (loud default,
/// never swallowed); anything else → generic 500, no stack, no details.
/// </summary>
internal static class ProblemMapping
{
    public static (int Status, ProblemDetails Problem) Map(Exception ex) => ex switch
    {
        CsAgentException cs when cs.Error.Code
            is "empty-store" or "corrupt-store" or "embedding-model-mismatch"
            => Problem(503, cs.Error.Component, cs.Error.Code, cs.Error.Message),
        CsAgentException cs => Problem(500, cs.Error.Component, cs.Error.Code, cs.Error.Message),
        Exception e when IsOrWraps<HttpRequestException>(e) || IsOrWraps<IOException>(e)
            || IsOrWraps<ClientResultException>(e)
            => Problem(502, "model", "transport",
                "A model call failed to reach the provider. Retry the ask."),
        _ => Problem(500, "internal", "unmapped", null),
    };

    /// <summary>Shutdown cancelled a queued or running job before a verdict existed.</summary>
    public static (int Status, ProblemDetails Problem) Cancelled() =>
        Problem(500, "http", "job-cancelled",
            "Job cancelled by server shutdown before a verdict existed.");

    private static (int, ProblemDetails) Problem(
        int status, string component, string name, string? detail) =>
        (status, new ProblemDetails
        {
            Status = status,
            Type = $"urn:cs-agent:{component}:{name}",
            Title = $"{component}/{name}",
            Detail = detail,
        });

    private static bool IsOrWraps<T>(Exception ex) where T : Exception
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            if (e is T)
                return true;
        return false;
    }
}