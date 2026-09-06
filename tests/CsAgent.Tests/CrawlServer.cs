using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsAgent.Tests;

/// <summary>Local Kestrel site: fixed routes, records hit paths + timestamps.</summary>
internal sealed class CrawlServer : IDisposable
{
    public sealed record Route(string Path, string Body, string ContentType = "text/html", int Status = 200);

    public sealed record Hit(string Path, DateTimeOffset At);

    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public ConcurrentQueue<Hit> Hits { get; } = new();

    public CrawlServer(params Route[] routes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use((ctx, next) =>
        {
            Hits.Enqueue(new Hit(ctx.Request.Path.Value!, DateTimeOffset.UtcNow));
            return next(ctx);
        });
        foreach (var route in routes)
        {
            var r = route;
            _app.MapGet(r.Path, () => r.Status == 200
                ? Results.Content(r.Body, r.ContentType)
                : Results.StatusCode(r.Status));
        }
        _app.Start();
        BaseUrl = _app.Urls.First();
    }

    public void Dispose()
    {
        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().GetAwaiter().GetResult();
    }
}