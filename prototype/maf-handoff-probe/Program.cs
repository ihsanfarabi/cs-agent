// MAF handoff probe — proves packages restore + the builder API compiles under .NET 10.
// Step-1 task: package/build gate only. Real handoff behavior goes to step-2 stub prototype.
#pragma warning disable MAAIW001

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

IChatClient BuildClient()
{
    var apiKey = Environment.GetEnvironmentVariable("CS_AGENT_MODEL_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("CS_AGENT_MODEL_KEY not set (probe still compiles/runs wiring without it — key needed for real calls).");
    var baseUrl = Environment.GetEnvironmentVariable("CS_AGENT_BASE_URL");
    if (string.IsNullOrWhiteSpace(baseUrl))
        return new OpenAI.OpenAIClient(apiKey).GetChatClient("gpt-5-mini").AsIChatClient();
    // Non-default base URL (go/no-go item 4): OpenAI-compatible endpoint (OpenRouter etc.)
    return new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
        .GetChatClient(Environment.GetEnvironmentVariable("CS_AGENT_VERIFY_MODEL") ?? "openai/gpt-4o-mini")
        .AsIChatClient();
}

var chatClient = BuildClient();

AIAgent triage = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Id = "triage",
    Name = "Triage",
    ChatOptions = new() { Instructions = "Route every support question to the Draft agent." },
});
AIAgent draft = chatClient.AsAIAgent(name: "Draft", instructions: "You answer support questions.");
AIAgent verify = chatClient.AsAIAgent(name: "Verify", instructions: "You check draft claims against retrieved chunks.");

Workflow workflow = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)
    .WithHandoff(triage, draft, handoffReason: "Route the question to the Draft agent")
    .WithHandoff(draft, verify, handoffReason: "Send the cited answer for claim checking")
    .Build();

Console.WriteLine($"probe ok: workflow '{workflow.Name}' built with 3 handoff participants");

// Item 4a: one real chat call through MAF.
AIAgent probeAgent = chatClient.AsAIAgent(name: "Probe", instructions: "You are a terse test agent.");
var response = await probeAgent.RunAsync("Reply with exactly: WIRING OK");
Console.WriteLine($"probe 4a chat: {response.Text}");

// Item 4b: structured output (standalone agent — verifier's shape).
var verdicts = await probeAgent.RunAsync<IReadOnlyList<ClaimVerdict>>(
    "Extract atomic claims from this sentence and judge each: 'Keys rotate from Settings. The old key stays valid 24 hours.'");
Console.WriteLine($"probe 4b structured: {verdicts.Result.Count} claims");
foreach (var v in verdicts.Result)
    Console.WriteLine($"  - {v.Claim} supported={v.Supported}");

// Item 4c: one real embedding call through the .NET embedding-generator seam.
var embeddingModel = Environment.GetEnvironmentVariable("CS_AGENT_EMBEDDING_MODEL") ?? "text-embedding-3-small";
var openAIClient = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS_AGENT_BASE_URL"))
    ? new OpenAI.OpenAIClient(Environment.GetEnvironmentVariable("CS_AGENT_MODEL_KEY")!)
    : new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(Environment.GetEnvironmentVariable("CS_AGENT_MODEL_KEY")!),
        new OpenAI.OpenAIClientOptions { Endpoint = new Uri(Environment.GetEnvironmentVariable("CS_AGENT_BASE_URL")!) });
var embeddingGen = openAIClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator();
var embedding = await embeddingGen.GenerateVectorAsync("How do I rotate the API key?");
var vec = embedding.Span;
Console.WriteLine($"probe 4c embedding: dim={vec.Length} first={vec[0]:F6}");

sealed record ClaimVerdict(string Claim, bool Supported);