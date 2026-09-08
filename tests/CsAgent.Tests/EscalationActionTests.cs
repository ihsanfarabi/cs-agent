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