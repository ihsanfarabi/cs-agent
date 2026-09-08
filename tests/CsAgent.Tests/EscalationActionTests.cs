using System.Text.Json;
using CsAgent.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// Fake chat client that scripts an MAF tool loop: the first response carries a
/// FunctionCallContent part (file_ticket, args {title, body}); every later
/// response is final text. ChatClientAgent wraps it with FunctionInvokingChatClient
/// and runs the tool loop against it — the fake-or-real model seam for tool tests.
/// </summary>
public sealed class ToolCallChatClient(string toolName, Dictionary<string, object?> arguments) : IChatClient
{
    private int _calls;
    private List<ChatMessage>? _lastMessages;

    public int CallCount => _calls;
    public IReadOnlyList<ChatMessage> LastMessages => _lastMessages ?? [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        _calls++;
        _lastMessages = [.. messages];
        if (_calls == 1)
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call_{_calls}", toolName, arguments)])));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ticket filed.")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Test-seam HttpMessageHandler: captures the last POST (URL, body) and answers
/// with a scripted response.
/// </summary>
public sealed class CapturingHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await respond(request, cancellationToken);
    }
}

/// <summary>
/// escalation_action on the ONE result object: null-omitted (default-off output
/// byte-identical across CLI --json / HTTP / eval), present when the
/// escalation agent acted. Same shared serializer as every surface.
/// </summary>
public sealed class EscalationActionTests
{
    private static AskResult Result(EscalationActionRecord? action = null) => new(
        "What is your SLA uptime?", false, null,
        [new MissingItem("SLA uptime is 99.9%", "no supporting chunk states this claim explicitly")],
        [], [], Calls: 2, Seconds: 1.0, CostEstimate: "— (non-default model)", EscalationAction: action);

    [Fact]
    public void NullOmitted_DefaultOffIsByteIdentical()
    {
        var json = JsonSerializer.Serialize(Result(), CsAgentJson.SerializerOptions);
        Assert.DoesNotContain("escalation_action", json);
        // 10-arg construction is unchanged (positional default) — deserializing
        // the default-off JSON round-trips without the field
        var roundtrip = JsonSerializer.Serialize(JsonSerializer.Deserialize<AskResult>(json, CsAgentJson.SerializerOptions), CsAgentJson.SerializerOptions);
        Assert.Equal(json, roundtrip);
    }

    [Fact]
    public void Present_SerializedUnderPinnedName()
    {
        var json = JsonSerializer.Serialize(Result(
            new EscalationActionRecord("file_ticket", "sent", "SLA uptime question", "HTTP 200")),
            CsAgentJson.SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var action = doc.RootElement.GetProperty("escalation_action");
        Assert.Equal("file_ticket", action.GetProperty("tool").GetString());
        Assert.Equal("sent", action.GetProperty("status").GetString());
        Assert.Equal("SLA uptime question", action.GetProperty("title").GetString());
        Assert.Equal("HTTP 200", action.GetProperty("detail").GetString());
    }
}

/// <summary>
/// The escalation dispatcher: a MAF agent with ONE tool (file_ticket) that POSTs
/// the ticket to a webhook. Never throws, never touches the verdict — the
/// outcome rides the result object. Default-off behavior is byte-identical.
/// </summary>
public sealed class EscalationDispatcherTests
{
    private static readonly Uri Webhook = new("https://hooks.example.com/x");

    private static EscalationContext Context() => new(
        "What is your SLA uptime?",
        [new MissingItem("SLA uptime is 99.9%", "no supporting chunk states this claim explicitly")],
        [new CitedChunk(1, "docs/api-keys.md", 0, "Rotate keys from Settings.", 0.5f)],
        "Rotate from Settings [1]. SLA uptime is 99.9%.");

    private EscalationContext? _factoryContext;
    private AITool? _factoryTool;

    /// <summary>Factory mirroring the runtime wiring: the tool is attached per dispatch.</summary>
    private AIAgent MakeFactoryAgent(EscalationContext context, AITool tool)
    {
        _factoryContext = context;
        _factoryTool = tool;
        return Client.AsAIAgent(name: "Escalation", tools: [tool]);
    }

    private ToolCallChatClient Client { get; } = new("file_ticket",
        new Dictionary<string, object?>
        {
            ["title"] = "SLA uptime question",
            ["body"] = "User asked about SLA uptime; docs lack it.",
        });

    private static CapturingHandler OkHandler() => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

    private static CapturingHandler Handler(System.Net.HttpStatusCode status) => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(status)));

    private EscalationDispatcher MakeDispatcher(CapturingHandler? handler = null, TimeSpan? postTimeout = null) =>
        new(MakeFactoryAgent, Webhook, handler ?? OkHandler(), postTimeout);

    [Fact]
    public void Dispatch_HealthyReceiver_RecordsSentWithReceipt()
    {
        var dispatcher = MakeDispatcher();

        var record = dispatcher.TryDispatch(Context());

        Assert.NotNull(record);
        Assert.Equal("file_ticket", record.Tool);
        Assert.Equal("sent", record.Status);
        Assert.Equal("SLA uptime question", record.Title);
        Assert.Equal("HTTP 200", record.Detail);
        Assert.Equal(2, Client.CallCount); // tool-call response + final text — loop ran to completion
    }

    [Fact]
    public void Dispatch_PostsToWebhook_JsonPostToPinnedUrl()
    {
        var handler = OkHandler();
        var dispatcher = MakeDispatcher(handler);

        dispatcher.TryDispatch(Context());

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(Webhook.ToString(), handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void Dispatch_PayloadCarriesContextFields()
    {
        var handler = OkHandler();
        var dispatcher = MakeDispatcher(handler);

        dispatcher.TryDispatch(Context());

        Assert.NotNull(handler.LastBody);
        using var payload = JsonDocument.Parse(handler.LastBody);
        var root = payload.RootElement;
        // code-enriched, not LLM-carried: context from EscalationContext, args from the agent
        Assert.Equal("cs-agent-escalation", root.GetProperty("tool").GetString());
        Assert.Equal("What is your SLA uptime?", root.GetProperty("question").GetString());
        Assert.Equal("SLA uptime question", root.GetProperty("title").GetString());
        Assert.Equal("User asked about SLA uptime; docs lack it.", root.GetProperty("body").GetString());
        Assert.Equal("SLA uptime is 99.9%", root.GetProperty("missing")[0].GetString());
        Assert.Equal("docs/api-keys.md", root.GetProperty("citations")[0].GetString());
        var timestamp = root.GetProperty("timestamp").GetString();
        Assert.NotNull(timestamp);
        Assert.EndsWith("Z", timestamp);
        Assert.True(DateTimeOffset.TryParse(timestamp, out _), $"timestamp not ISO-8601: {timestamp}");
    }

    [Fact]
    public void Dispatch_PromptCarriesQuestionMissingPagesDraft()
    {
        var prompt = EscalationDispatcher.FormatPrompt(Context());
        Assert.Contains("QUESTION: What is your SLA uptime?", prompt);
        Assert.Contains("SLA uptime is 99.9%", prompt);                            // missing, one per line
        Assert.Contains("docs/api-keys.md", prompt);                              // nearest pages
        Assert.Contains("Rotate from Settings [1]. SLA uptime is 99.9%.", prompt); // rejected draft, context only
        Assert.Contains("REJECTED DRAFT (context only, do not re-answer):", prompt);
    }

    [Fact]
    public void Factory_ReceivesPerDispatchContextAndTool()
    {
        var dispatcher = MakeDispatcher();
        var context = Context();

        dispatcher.TryDispatch(context);

        Assert.Equal(context, _factoryContext); // the dispatcher hands the factory its context...
        Assert.NotNull(_factoryTool);           // ...and its fresh per-dispatch tool closure
        Assert.Equal("file_ticket", _factoryTool!.Name);
    }

    [Fact]
    public void EscalationInstructions_CarryPinnedTemplate()
    {
        var instructions = Prompts.EscalationInstructions();
        Assert.Contains("file_ticket", instructions);
        Assert.Contains("QUESTION: {question}", instructions);
        Assert.Contains("Do not answer the question.", instructions);
    }

    /// <summary>Never completes, ignores cancellation — the observed stall shape.</summary>
    private sealed class StalledChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => new TaskCompletionSource<ChatResponse>().Task;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private EscalationDispatcher MakeStalledDispatcher(CapturingHandler? handler = null,
        TimeSpan? modelTimeout = null, TimeSpan? postTimeout = null) =>
        new((_, tool) => new StalledChatClient().AsAIAgent(name: "Escalation", tools: [tool]),
            Webhook, handler, postTimeout, modelTimeout);

    [Fact]
    public void Non2xxResponse_RecordsFailedNamingTheCode()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));
        var dispatcher = MakeDispatcher(handler);

        var record = dispatcher.TryDispatch(Context());

        Assert.NotNull(record);
        Assert.Equal("failed", record.Status);
        Assert.Equal("SLA uptime question", record.Title); // the tool ran; the agent's title is kept
        Assert.Equal("HTTP 500", record.Detail);
    }

    [Fact]
    public void PostTimeout_RecordsFailedNamingTheTimeout()
    {
        var handler = new CapturingHandler(async (_, ct) =>
        {
            await Task.Delay(10_000, ct); // slower than the POST ceiling below
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var dispatcher = MakeDispatcher(handler, postTimeout: TimeSpan.FromMilliseconds(200));

        var record = dispatcher.TryDispatch(Context());

        Assert.NotNull(record);
        Assert.Equal("failed", record.Status);
        Assert.Equal("SLA uptime question", record.Title);
        Assert.Contains("timed out", record.Detail);
    }

    [Fact]
    public void AgentNeverCallsTool_RecordsFailedWithNullTitle()
    {
        var dispatcher = new EscalationDispatcher(
            (_, tool) => new FakeChatClient(_ => "I cannot file tickets.").AsAIAgent(name: "Escalation", tools: [tool]),
            Webhook, OkHandler());

        var record = dispatcher.TryDispatch(Context());

        Assert.NotNull(record);
        Assert.Equal("file_ticket", record.Tool);
        Assert.Equal("failed", record.Status);
        Assert.Null(record.Title); // the agent never called the tool — no fabricated title
        Assert.Equal("escalation agent did not call file_ticket", record.Detail);
    }

    [Fact]
    public void AgentStall_RecordsTimeoutFailedNotAWedge()
    {
        var dispatcher = MakeStalledDispatcher(modelTimeout: TimeSpan.FromMilliseconds(200));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var record = dispatcher.TryDispatch(Context());

        Assert.NotNull(record);
        Assert.Equal("failed", record.Status);
        Assert.Null(record.Title);
        Assert.Contains("did not complete within", record.Detail); // the 240s ceiling, surfaced
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"escalation agent stall must be bounded by the model-call ceiling, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task CancelledDuringDispatch_ReturnsNullNoFabricatedRecord()
    {
        var dispatcher = MakeStalledDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var record = await Task.Run(() => dispatcher.TryDispatch(Context(), cts.Token));

        Assert.Null(record); // shutdown: cancellation propagates as null, never a fabricated failed record
    }
}

/// <summary>
/// Pipeline wiring: the dispatcher runs only after an escalate verdict, records
/// its outcome on the result object, and counts as a model call. Default-off
/// (no dispatcher) stays byte-identical; a resolve verdict never dispatches.
/// </summary>
public sealed class EscalationPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-escalation-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private const string ResolvedJson =
        """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]},{"claim":"Old keys stay valid 24 hours","supported":true,"supporting_chunk_ids":[1]}]""";
    private const string EscalateJson =
        """[{"claim":"Keys rotate from Settings","supported":true,"supporting_chunk_ids":[1]},{"claim":"SLA uptime is 99.9%","supported":false,"supporting_chunk_ids":[]}]""";
    private const string EscalateDraft = "Rotate from Settings [1]. SLA uptime is 99.9%.";

    private void SeedStore()
    {
        using var store = new SqliteVectorStore(_dbPath, "fake-embedding");
        store.UpsertPage("docs/api-keys.md", "h1", new[]
        {
            ("Rotate keys from Settings → API keys → Rotate.", FakeEmbeddingGenerator.HashToVector("rotate settings")),
            ("Keys inherit the team role. Read-only keys cannot deploy.", FakeEmbeddingGenerator.HashToVector("roles permissions")),
        });
    }

    private AskPipeline MakePipeline(string verifyJson, EscalationDispatcher? escalation = null)
    {
        SeedStore();
        var retriever = new Retriever(
            new FakeEmbeddingGenerator(), new SqliteVectorStore(_dbPath, "fake-embedding"), topK: 5);
        ChatClientAgent Draft() => new(new FakeChatClient(_ => EscalateDraft));
        ChatClientAgent Verify() => new(new FakeChatClient(_ => verifyJson));
        return new AskPipeline(Draft, Verify, retriever, escalation: escalation);
    }

    private static (EscalationDispatcher Dispatcher, ToolCallChatClient Client) MakeDispatcher(CapturingHandler handler)
    {
        var client = new ToolCallChatClient("file_ticket", new Dictionary<string, object?>
        {
            ["title"] = "SLA uptime question",
            ["body"] = "User asked about SLA uptime; docs lack it.",
        });
        return (new EscalationDispatcher(
            (_, tool) => client.AsAIAgent(name: "Escalation", tools: [tool]),
            new Uri("https://hooks.example.com/x"), handler), client);
    }

    [Fact]
    public void Escalate_Enabled_RecordsActionVerdictUntouchedCallsPlusOne()
    {
        // baseline: same ask with the feature off — the verdict must not move
        var baseline = MakePipeline(EscalateJson).Run("What is your SLA uptime?");
        // the receiver takes real time (500ms) — proving seconds includes the action
        var handler = new CapturingHandler(async (_, ct) =>
        {
            await Task.Delay(500, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var (dispatcher, _) = MakeDispatcher(handler);
        var result = MakePipeline(EscalateJson, dispatcher).Run("What is your SLA uptime?");

        Assert.False(result.Resolved);
        Assert.NotNull(result.EscalationAction);
        Assert.Equal("sent", result.EscalationAction!.Status);
        Assert.Equal("SLA uptime question", result.EscalationAction.Title);
        Assert.Equal("HTTP 200", result.EscalationAction.Detail);
        Assert.Equal(baseline.Calls + 1, result.Calls); // the escalation agent is the 4th call
        // verdict byte-identical to the no-action escalate result
        Assert.Equal(baseline.Question, result.Question);
        Assert.Equal(baseline.Resolved, result.Resolved);
        Assert.Equal(baseline.Answer, result.Answer);
        Assert.Equal(baseline.Missing, result.Missing);
        // claims carry int[] fields (reference equality) — compare on the wire form
        Assert.Equal(
            JsonSerializer.Serialize(baseline.Claims, CsAgentJson.SerializerOptions),
            JsonSerializer.Serialize(result.Claims, CsAgentJson.SerializerOptions));
        Assert.Equal(baseline.CitedChunks, result.CitedChunks);
        // the 500ms receiver sits inside the measured window (Task.Delay never fires
        // early; the margin only absorbs scheduling noise) — jitter-proof pin on
        // "stopwatch stops after the action"
        Assert.True(result.Seconds >= 0.45, $"seconds must include the action (honest wall time), got {result.Seconds}");
        Assert.NotNull(handler.LastBody); // the ticket actually POSTed
    }

    [Fact]
    public void Resolve_Enabled_DispatcherNeverInvoked()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var (dispatcher, escalationClient) = MakeDispatcher(handler);
        var result = MakePipeline(ResolvedJson, dispatcher).Run("How do I rotate the API key?");

        Assert.True(result.Resolved);
        Assert.Null(result.EscalationAction); // a resolve verdict dispatches nothing
        Assert.Equal(2, result.Calls);
        Assert.Equal(0, escalationClient.CallCount);
        Assert.Null(handler.LastRequest); // no POST ever left the process
    }

    [Fact]
    public void Off_NoEscalationActionInSerializedResult()
    {
        var result = MakePipeline(EscalateJson).Run("What is your SLA uptime?");
        Assert.Null(result.EscalationAction);
        var json = JsonSerializer.Serialize(result, CsAgentJson.SerializerOptions);
        Assert.DoesNotContain("escalation_action", json); // default-off is byte-identical to today
    }
}