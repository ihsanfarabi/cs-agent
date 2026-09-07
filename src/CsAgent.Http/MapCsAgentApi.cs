using CsAgent.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CsAgent.Http;

/// <summary>
/// The HTTP surface: 202 /ask + GET /result/{id} polling (design increment
/// D17 — sync /ask replaced entirely), GET /health. Status is carried in the
/// body, not the HTTP code; 5xx stays reserved for real errors (D19).
/// One result object (AskResult) serialized with the shared Core options —
/// byte-identical to `ask --json` by construction (eng-review D6).
/// </summary>
public static class CsAgentApiExtensions
{
    public static IEndpointRouteBuilder MapCsAgentApi(this IEndpointRouteBuilder app, AskPipeline pipeline)
    {
        var lifetime = app.ServiceProvider.GetService<IHostApplicationLifetime>();
        var store = new AskJobStore(pipeline, lifetime?.ApplicationStopping ?? CancellationToken.None);

        app.MapPost("/ask", async (HttpContext http) =>
        {
            AskRequest? request;
            try { request = await http.Request.ReadFromJsonAsync<AskRequest>(); }
            catch (System.Text.Json.JsonException)
            {
                return Bad(400, "http", "malformed-body",
                    "Request body must be JSON: { \"question\": \"...\" }.");
            }
            if (request is null || string.IsNullOrWhiteSpace(request.Question))
                return Bad(400, "http", "missing-question",
                    "The 'question' field is required and must not be empty.");

            var id = store.Submit(request.Question);
            return Results.Json(new { id, status = "running", result = $"/result/{id}" }, statusCode: 202);
        });

        app.MapGet("/result/{id}", (string id) =>
        {
            var job = store.Get(id);
            if (job is null)
                return Bad(404, "http", "unknown-job",
                    $"No ask job '{id}' — ids are in-memory and lost on restart. Resubmit to /ask.");

            return job.Status switch
            {
                AskJobStatus.Running => Results.Json(new { id = job.Id, status = "running" }),
                AskJobStatus.Done => Results.Json(job.Result, CsAgentJson.SerializerOptions),
                AskJobStatus.Errored => Results.Json(job.Error, statusCode: job.ErrorStatus ?? 500),
                _ => Bad(500, "internal", "unmapped", null),
            };
        });

        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        return app;
    }

    private static IResult Bad(int status, string component, string name, string? detail) =>
        Results.Json(new ProblemDetails
        {
            Status = status,
            Type = $"urn:cs-agent:{component}:{name}",
            Title = $"{component}/{name}",
            Detail = detail,
        }, statusCode: status);
}

/// <summary>POST /ask body: { "question": string }.</summary>
public sealed record AskRequest(string? Question);