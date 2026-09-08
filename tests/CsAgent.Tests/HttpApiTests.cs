using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;
using CsAgent.Core;
using CsAgent.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// HTTP API suite on the fake-or-real model seam, via real Kestrel bound to
/// an ephemeral loopback port (same host wiring as `serve`: slim builder,
/// server header off). Harness regressions only — same honest scope as the
/// existing eval gates.
/// </summary>
public sealed class HttpApiTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-http-{Guid.NewGuid():N}.db");
    private WebApplication? _app;

    public void Dispose()
    {
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private const string ResolvedJson =
        """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]},{"claim":"Old keys stay valid 24 hours","supported":true,"supporting_chunk_ids":[1]}]""";
    private const string EscalateJson =
        """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]},{"claim":"SLA uptime is 99.9%","supported":false,"supporting_chunk_ids":[]}]""";

    private static SqliteVectorStore NewStore(string path) => new(path, "fake-embedding");

    private void SeedStore()
    {
        using var store = NewStore(_dbPath);
        store.UpsertPage("docs/api-keys.md", "h1", new[]
        {
            ("Rotate keys from Settings → API keys → Rotate.", FakeEmbeddingGenerator.HashToVector("rotate settings")),
            ("Keys inherit the team role. Read-only keys cannot deploy.", FakeEmbeddingGenerator.HashToVector("roles permissions")),
        });
    }

    private AskPipeline MakePipeline(
        string draftAnswer, string verifyJson, bool seedStore = true,
        EscalationDispatcher? escalation = null)
    {
        if (seedStore) SeedStore();
        else _ = NewStore(_dbPath); // schema-only store: empty, but openable (no stray-file lie)
        var retriever = new Retriever(new FakeEmbeddingGenerator(), NewStore(_dbPath), topK: 5);
        ChatClientAgent Draft() => new(new FakeChatClient(_ => draftAnswer));
        ChatClientAgent Verify() => new(new FakeChatClient(_ => verifyJson));
        return new AskPipeline(Draft, Verify, retriever, escalation: escalation);
    }

    /// <summary>Dispatcher against a capturing handler: the ticket loop runs in-process.</summary>
    private static (EscalationDispatcher Dispatcher, CapturingHandler Handler) MakeEscalation()
    {
        var client = new ToolCallChatClient("file_ticket", new Dictionary<string, object?>
        {
            ["title"] = "SLA uptime question",
            ["body"] = "User asked about SLA uptime; docs lack it.",
        });
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        return (new EscalationDispatcher(
            (_, tool) => client.AsAIAgent(name: "Escalation", tools: [tool]),
            new Uri("https://hooks.example.com/x"), handler), handler);
    }

    private AskPipeline MakeThrowingPipeline(
        Func<IEnumerable<ChatMessage>, string> draftRespond,
        Func<IEnumerable<ChatMessage>, string> verifyRespond)
    {
        SeedStore();
        var retriever = new Retriever(new FakeEmbeddingGenerator(), NewStore(_dbPath), topK: 5);
        ChatClientAgent Draft() => new(new FakeChatClient(draftRespond));
        ChatClientAgent Verify() => new(new FakeChatClient(verifyRespond));
        return new AskPipeline(Draft, Verify, retriever);
    }

    /// <summary>Same host wiring as ServeRunner: slim builder, no server header, loopback bind.</summary>
    private async Task<HttpClient> StartAsync(AskPipeline pipeline)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral loopback port
        var app = builder.Build();
        app.MapCsAgentApi(pipeline);
        await app.StartAsync();
        _app = app;
        return new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<string> SubmitAsync(HttpClient client, string question)
    {
        using var response = await client.PostAsync("/ask",
            new StringContent($"{{\"question\": \"{question}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("running", body.GetProperty("status").GetString());
        Assert.Equal($"/result/{body.GetProperty("id").GetString()}", body.GetProperty("result").GetString());
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>Poll until the job leaves running: done (200 AskResult) or errored (mapped 5xx).</summary>
    private static async Task<HttpResponseMessage> PollSettledAsync(
        HttpClient client, string id, int seconds = 30, string query = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/result/{id}{query}");
            if ((int)response.StatusCode >= 500)
                return response; // errored: recorded registry status replayed
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await ReadJsonAsync(response);
                if (!body.TryGetProperty("status", out _))
                    return response; // done: canonical AskResult has no status field
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"job {id} did not settle within {seconds}s");
    }

    private static void AssertNoStackOrServerHeader(HttpResponseMessage response, string body)
    {
        Assert.DoesNotContain("   at ", body); // no stack trace ever reaches a response body
        Assert.False(response.Headers.Contains("Server"), "server header must be suppressed");
    }

    [Fact]
    public async Task SubmitAccepted_PollsToDone_ContractByteIdentical()
    {
        using var client = await StartAsync(MakePipeline(
            "Rotate keys from Settings → API keys → Rotate. Old keys stay valid 24 hours. [1]", ResolvedJson));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var done = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var body = await done.Content.ReadAsStringAsync();
        // contract test: HTTP body byte-identical to canonical serialization of the
        // same result object — proves the surface uses the shared Core options
        // (indentation + pinned property names), so it equals `ask --json`
        var result = JsonSerializer.Deserialize<AskResult>(body, CsAgentJson.SerializerOptions);
        Assert.NotNull(result);
        Assert.True(result.Resolved);
        Assert.Equal(2, result.Calls);
        Assert.Equal(JsonSerializer.Serialize(result, CsAgentJson.SerializerOptions), body);
    }

    [Fact]
    public async Task Escalate_IsDone200_WithMissing()
    {
        using var client = await StartAsync(MakePipeline(
            "Rotate from Settings [1]. SLA uptime is 99.9%.", EscalateJson));

        var id = await SubmitAsync(client, "What is your SLA uptime?");
        using var done = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode); // escalate is a 200 done, not a 5xx
        var result = JsonSerializer.Deserialize<AskResult>(
            await done.Content.ReadAsStringAsync(), CsAgentJson.SerializerOptions)!;
        Assert.False(result.Resolved);
        Assert.Null(result.Answer); // rejected draft never surfaces
        Assert.NotEmpty(result.Missing!);
    }

    [Fact]
    public async Task EscalateWithAction_PollCarriesEscalationAction()
    {
        var (escalation, handler) = MakeEscalation();
        using var client = await StartAsync(MakePipeline(
            "Rotate from Settings [1]. SLA uptime is 99.9%.", EscalateJson, escalation: escalation));

        var id = await SubmitAsync(client, "What is your SLA uptime?");
        using var done = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var body = await done.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AskResult>(body, CsAgentJson.SerializerOptions)!;
        Assert.False(result.Resolved);
        Assert.NotNull(result.EscalationAction); // the action rides the canonical result, free
        Assert.Equal("sent", result.EscalationAction!.Status);
        Assert.Equal("SLA uptime question", result.EscalationAction.Title);
        Assert.Equal(3, result.Calls); // verdict calls + the escalation agent
        // byte-identical to canonical serialization — the surface adds nothing
        Assert.Equal(JsonSerializer.Serialize(result, CsAgentJson.SerializerOptions), body);
        Assert.NotNull(handler.LastBody); // the POST left through the dispatcher
    }

    [Fact]
    public async Task EscalateWithoutAction_BarePollHasNoEscalationAction()
    {
        using var client = await StartAsync(MakePipeline(
            "Rotate from Settings [1]. SLA uptime is 99.9%.", EscalateJson));

        var id = await SubmitAsync(client, "What is your SLA uptime?");
        using var done = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var body = await done.Content.ReadAsStringAsync();
        Assert.DoesNotContain("escalation_action", body); // feature off: today's bytes, unchanged
        var result = JsonSerializer.Deserialize<AskResult>(body, CsAgentJson.SerializerOptions)!;
        Assert.False(result.Resolved);
        Assert.Null(result.EscalationAction);
        Assert.Equal(JsonSerializer.Serialize(result, CsAgentJson.SerializerOptions), body);
    }

    [Fact]
    public async Task MalformedBody_400_NoStack()
    {
        using var client = await StartAsync(MakePipeline("Rotate [1].", ResolvedJson));
        using var response = await client.PostAsync("/ask",
            new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:http:malformed-body", body);
        AssertNoStackOrServerHeader(response, body);
    }

    [Fact]
    public async Task MissingQuestion_400_NoStack()
    {
        using var client = await StartAsync(MakePipeline("Rotate [1].", ResolvedJson));
        using var response = await client.PostAsync("/ask",
            new StringContent("{\"question\": \"  \"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:http:missing-question", body);
        AssertNoStackOrServerHeader(response, body);
    }

    [Fact]
    public async Task UnknownJob_404()
    {
        using var client = await StartAsync(MakePipeline("Rotate [1].", ResolvedJson));
        using var response = await client.GetAsync("/result/deadbeef");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:http:unknown-job", body);
        AssertNoStackOrServerHeader(response, body);
    }

    [Fact]
    public async Task DraftTransport_WrappedTwice_502ErroredJob()
    {
        // wrapper layers (M.E.AI, OpenAI SDK) re-throw in their own type with the
        // transport error nested — the is-or-wraps rule matches at any depth
        var transport = new HttpRequestException("connection refused");
        var wrapped = new InvalidOperationException("client wrapper", new ArgumentException("inner wrapper", transport));
        using var client = await StartAsync(MakeThrowingPipeline(
            _ => throw wrapped, _ => ResolvedJson));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var errored = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.BadGateway, errored.StatusCode);
        var body = await errored.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:model:transport", body);
        AssertNoStackOrServerHeader(errored, body);
    }

    [Fact]
    public async Task ClientResultExceptionBare_502ErroredJob()
    {
        // pinned from the dead-BaseUrl live check (eng-review D4): the OpenAI /
        // ClientModel retry path surfaces transport failures as a bare
        // ClientResultException with NO inner exception
        using var client = await StartAsync(MakeThrowingPipeline(
            _ => throw new ClientResultException("Connection refused (127.0.0.1:9)"),
            _ => ResolvedJson));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var errored = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.BadGateway, errored.StatusCode);
        var body = await errored.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:model:transport", body);
        AssertNoStackOrServerHeader(errored, body);
    }

    [Fact]
    public async Task VerifyTransport_FailsClosedToEscalateDone200()
    {
        // verify-stage transport is swallowed by VerifyOnce and fails closed —
        // escalate 200, unchanged; the missing-note misattribution is the
        // documented Core caveat, not fixed here
        using var client = await StartAsync(MakeThrowingPipeline(
            _ => "Rotate from Settings [1].",
            _ => throw new HttpRequestException("connection refused")));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var done = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var result = JsonSerializer.Deserialize<AskResult>(
            await done.Content.ReadAsStringAsync(), CsAgentJson.SerializerOptions)!;
        Assert.False(result.Resolved);
        Assert.Null(result.Answer);
        var missing = Assert.Single(result.Missing!);
        Assert.Contains("malformed", missing.Note); // documented misattribution caveat, pinned
    }

    [Fact]
    public async Task EmptyStore_Retrieve_503ErroredJob()
    {
        // library-level defense: through `serve` an empty store never reaches a
        // request (startup fail-fast) — this pins the mid-run mapping anyway
        using var client = await StartAsync(MakePipeline("Rotate [1].", ResolvedJson, seedStore: false));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var errored = await PollSettledAsync(client, id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, errored.StatusCode);
        var body = await errored.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:store:empty-store", body);
        AssertNoStackOrServerHeader(errored, body);
    }

    [Fact]
    public async Task ConcurrentSubmits_SerializeBehindTheGate()
    {
        var inside = 0;
        var overlapped = false;
        using var client = await StartAsync(MakeThrowingPipeline(
            _ =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                    overlapped = true; // two asks inside the pipeline at once
                Thread.Sleep(50); // widen the window so real overlap would be caught
                Interlocked.Decrement(ref inside);
                return "Rotate keys from Settings. Old keys stay valid 24 hours. [1]";
            },
            _ => ResolvedJson));

        var id1 = await SubmitAsync(client, "How do I rotate the API key?");
        var id2 = await SubmitAsync(client, "How long do old keys stay valid?");

        using var done1 = await PollSettledAsync(client, id1);
        using var done2 = await PollSettledAsync(client, id2);

        Assert.Equal(HttpStatusCode.OK, done1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, done2.StatusCode);
        Assert.False(overlapped); // one pipeline at a time (singleton, eng-review D7)
    }

    [Fact]
    public async Task Health_Ok_ZeroModelCalls()
    {
        var draft = new FakeChatClient(_ => "answer");
        var verify = new FakeChatClient(_ => ResolvedJson);
        SeedStore();
        var retriever = new Retriever(new FakeEmbeddingGenerator(), NewStore(_dbPath), topK: 5);
        ChatClientAgent Draft() => new(draft);
        ChatClientAgent Verify() => new(verify);
        using var client = await StartAsync(new AskPipeline(Draft, Verify, retriever));

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, draft.CallCount);
        Assert.Equal(0, verify.CallCount);
    }

    [Fact]
    public async Task EvidencePoll_EnvelopeResultByteIdenticalToBarePoll()
    {
        using var client = await StartAsync(MakePipeline(
            "Rotate keys from Settings → API keys → Rotate. Old keys stay valid 24 hours. [1]", ResolvedJson));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var done = await PollSettledAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var bare = await done.Content.ReadAsStringAsync();

        // same job, evidence poll: canonical result nested, byte-identical
        using var withEvidence = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.OK, withEvidence.StatusCode);
        var envelope = JsonSerializer.Deserialize<AskEvidenceEnvelope>(
            await withEvidence.Content.ReadAsStringAsync(), CsAgentJson.SerializerOptions)!;
        Assert.True(envelope.Result.Resolved);
        Assert.Equal(bare, JsonSerializer.Serialize(envelope.Result, CsAgentJson.SerializerOptions));
        Assert.Equal(ResolvedJson, envelope.Evidence.VerifierRaw);
        Assert.False(envelope.Evidence.DraftRejected);
        Assert.Contains("Rotate keys", envelope.Evidence.DraftAnswer);
    }

    [Fact]
    public async Task EscalatedEvidencePoll_RejectedDraftGatedUnderEvidence()
    {
        const string draft = "Rotate from Settings [1]. SLA uptime is 99.9%.";
        using var client = await StartAsync(MakePipeline(draft, EscalateJson));

        var id = await SubmitAsync(client, "What is your SLA uptime?");
        using var done = await PollSettledAsync(client, id);
        using var withEvidence = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.OK, withEvidence.StatusCode);

        var envelope = JsonSerializer.Deserialize<AskEvidenceEnvelope>(
            await withEvidence.Content.ReadAsStringAsync(), CsAgentJson.SerializerOptions)!;
        Assert.False(envelope.Result.Resolved);
        Assert.Null(envelope.Result.Answer); // rejected draft never surfaces in the canonical result
        Assert.Equal(draft, envelope.Evidence.DraftAnswer); // ...only inside evidence, explicitly gated
        Assert.True(envelope.Evidence.DraftRejected);
        Assert.NotEmpty(envelope.Result.Missing!);
    }

    [Fact]
    public async Task EvidencePollWhileRunning_StillRunningShape()
    {
        // draft blocks until released — pins the running poll deterministically
        // (evidence flag must not change the running body; verdict-before-output:
        // no trace exists before Done)
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var client = await StartAsync(MakeThrowingPipeline(
            _ => { started.Set(); release.Wait(10_000); return "Rotate from Settings [1]."; },
            _ => ResolvedJson));

        var id = await SubmitAsync(client, "How do I rotate the API key?");
        Assert.True(started.Wait(10_000), "draft model call never started");
        using var running = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        var body = await ReadJsonAsync(running);
        Assert.Equal("running", body.GetProperty("status").GetString());

        release.Set();
        using var settled = await PollSettledAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
    }

    [Fact]
    public async Task EvidencePollUnknownJob_404()
    {
        using var client = await StartAsync(MakePipeline("Rotate [1].", ResolvedJson));
        using var response = await client.GetAsync("/result/deadbeef?evidence=true");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("urn:cs-agent:http:unknown-job", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EvidencePollOnErroredJob_Replays5xxNoEnvelope()
    {
        using var client = await StartAsync(MakeThrowingPipeline(
            _ => throw new HttpRequestException("connection refused"), _ => ResolvedJson));
        var id = await SubmitAsync(client, "How do I rotate the API key?");
        using var errored = await PollSettledAsync(client, id);
        Assert.Equal(HttpStatusCode.BadGateway, errored.StatusCode);

        using var withEvidence = await client.GetAsync($"/result/{id}?evidence=true");
        Assert.Equal(HttpStatusCode.BadGateway, withEvidence.StatusCode); // replay, unchanged
        var body = await withEvidence.Content.ReadAsStringAsync();
        Assert.Contains("urn:cs-agent:model:transport", body);
        Assert.DoesNotContain("\"evidence\"", body); // an errored job has no trace — no fabricated bundle
    }

    [Fact]
    public async Task ServerHeader_AbsentOnAccepted()
    {
        using var client = await StartAsync(MakePipeline("Rotate [1].", ResolvedJson));
        using var response = await client.PostAsync("/ask",
            new StringContent("{\"question\": \"q\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False(response.Headers.Contains("Server"));
    }
}