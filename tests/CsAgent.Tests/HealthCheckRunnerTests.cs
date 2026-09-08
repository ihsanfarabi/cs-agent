using System.Net;
using System.Net.Sockets;
using CsAgent.Cli;
using CsAgent.Core;
using CsAgent.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// `cs-agent healthcheck [--port N]` — the in-image HEALTHCHECK probe (chiseled
/// base ships no shell/curl, so the image probes through the CLI itself).
/// Same port precedence as serve (--port, then CS_AGENT_PORT, then 5123), target
/// always loopback (the probe runs inside the container). Zero model calls,
/// zero store access — named structured error + exit 1 on every failure,
/// success is silent (HEALTHCHECK logs stay clean).
/// </summary>
public sealed class HealthCheckRunnerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-health-{Guid.NewGuid():N}.db");
    private WebApplication? _app;

    public void Dispose()
    {
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static SqliteVectorStore NewStore(string path) => new(path, "fake-embedding");

    private async Task<int> StartServerAsync(Func<int, Task<IResult>>? handler = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral loopback port
        var app = builder.Build();
        if (handler is null)
            app.MapCsAgentApi(AskPipeline()); // real mapping: /health exists
        else
            app.MapGet("/health", () => handler(0));
        await app.StartAsync();
        _app = app;
        var port = new Uri(app.Urls.Single()).Port;
        return port;
    }

    private AskPipeline AskPipeline()
    {
        using (var store = NewStore(_dbPath))
            store.UpsertPage("docs/api-keys.md", "h1",
                [("Rotate keys from Settings.", FakeEmbeddingGenerator.HashToVector("rotate"))]);
        var draft = new FakeChatClient(_ => "Rotate keys from Settings. [1]");
        var verify = new FakeChatClient(_ =>
            """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]}]""");
        var retriever = new Retriever(new FakeEmbeddingGenerator(), NewStore(_dbPath), topK: 5);
        return new AskPipeline(() => new ChatClientAgent(draft), () => new ChatClientAgent(verify), retriever);
    }

    private static void WithHealthEnv(string? port, Action act)
    {
        var prev = Environment.GetEnvironmentVariable("CS_AGENT_PORT");
        Environment.SetEnvironmentVariable("CS_AGENT_PORT", port);
        try { act(); }
        finally { Environment.SetEnvironmentVariable("CS_AGENT_PORT", prev); }
    }

    private static string CaptureStderr(Action act)
    {
        var origErr = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try { act(); }
        finally { Console.SetError(origErr); }
        return captured.ToString();
    }

    [Fact]
    public async Task ServerOk_Returns0_ZeroModelCalls()
    {
        var port = await StartServerAsync();
        var draft = new FakeChatClient(_ => "answer");
        Assert.Equal(0, HealthCheckRunner.Run(["healthcheck", "--port", $"{port}"]));
        Assert.Equal(0, draft.CallCount);
    }

    [Fact]
    public async Task PortEnvHonored_NoFlag()
    {
        var port = await StartServerAsync();
        WithHealthEnv($"{port}", () => Assert.Equal(0, HealthCheckRunner.Run(["healthcheck"])));
    }

    [Fact]
    public async Task ConnectionRefused_Returns1_NamedUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // port now free — connect must refuse

        var stderr = CaptureStderr(() =>
            Assert.Equal(1, HealthCheckRunner.Run(["healthcheck", "--port", $"{port}"])));
        Assert.Contains("healthcheck", stderr);
        Assert.Contains("unreachable", stderr);
    }

    [Fact]
    public async Task Non200_Returns1_NamedBadStatus()
    {
        var port = await StartServerAsync(
            async _ => Results.StatusCode(StatusCodes.Status500InternalServerError));

        var stderr = CaptureStderr(() =>
            Assert.Equal(1, HealthCheckRunner.Run(["healthcheck", "--port", $"{port}"])));
        Assert.Contains("bad-status", stderr);
        Assert.Contains("500", stderr);
    }

    [Theory]
    [InlineData("healthcheck --port not-a-port")]
    [InlineData("healthcheck --port 70000")]
    [InlineData("healthcheck --port")]
    [InlineData("healthcheck extra")]
    public void BadArgsOrPort_Returns1_Named(string argString)
    {
        var args = argString.Split(' ');
        var stderr = CaptureStderr(() => Assert.Equal(1, HealthCheckRunner.Run(args)));
        Assert.Contains("healthcheck", stderr);
    }

    [Fact]
    public void BadPortEnv_Returns1_NamesEnvVar()
    {
        // validate loudly: a bad CS_AGENT_PORT is a named refusal, not a silent default
        var stderr = CaptureStderr(() =>
            WithHealthEnv("bogus", () => Assert.Equal(1, HealthCheckRunner.Run(["healthcheck"]))));
        Assert.Contains("CS_AGENT_PORT", stderr);
    }

    [Fact]
    public async Task SlowEndpoint_Returns1_NamedTimeout()
    {
        var port = await StartServerAsync(async _ =>
        {
            await Task.Delay(10_000);
            return Results.Ok();
        });

        var stderr = CaptureStderr(() =>
            Assert.Equal(1, HealthCheckRunner.Run(["healthcheck", "--port", $"{port}"],
                timeout: TimeSpan.FromMilliseconds(200))));
        Assert.Contains("timeout", stderr);
    }
}