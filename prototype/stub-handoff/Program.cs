#pragma warning disable MAAIW001

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using MafStubHandoff;

// Stub handoff prototype — go/no-go items 1-3 with scripted fake models.
// Modes: resolve | escalate | devui
var mode = args.FirstOrDefault() ?? "resolve";

Workflow BuildWorkflow(bool verifyEscalates, bool triageUngated = false)
{
    AIAgent triage = triageUngated
        ? ScriptedClientFactory.Create("triage",
                new ScriptedTurn(Text: "Ungated answer attempt: rotate keys from Settings."))
            .AsAIAgent(new ChatClientAgentOptions
            {
                Id = "triage",
                Name = "Triage",
                ChatOptions = new() { Instructions = "Route every support question to the Draft agent." },
            })
        : ScriptedClientFactory.Create("triage",
            new ScriptedTurn(Text: "Routing to Draft.", HandoffTo: "draft", HandoffReason: "Route every support question to the Draft agent"))
        .AsAIAgent(new ChatClientAgentOptions
        {
            Id = "triage",
            Name = "Triage",
            ChatOptions = new() { Instructions = "Route every support question to the Draft agent." },
        });

    AIAgent draft = ScriptedClientFactory.Create("draft",
            new ScriptedTurn(Text: "Rotate keys from Settings → API keys → Rotate. Old key valid 24h. [1][2]",
                HandoffTo: "verify", HandoffReason: "Send the cited answer for claim checking"))
        .AsAIAgent(new ChatClientAgentOptions
        {
            Id = "draft",
            Name = "Draft",
            ChatOptions = new() { Instructions = "You write cited answers." },
        });

    AIAgent verify = verifyEscalates
        ? ScriptedClientFactory.Create("verify",
                new ScriptedTurn(Text: "Claim 'SLA uptime percentage' unsupported.", HandoffTo: "escalation",
                    HandoffReason: "Any-unsupported claim — hand off for escalation"))
            .AsAIAgent(new ChatClientAgentOptions
            {
                Id = "verify",
                Name = "Verify",
                ChatOptions = new() { Instructions = "You judge atomic claims against cited chunks." },
            })
        : ScriptedClientFactory.Create("verify",
                new ScriptedTurn(Text: "All 4 claims supported."))
            .AsAIAgent(new ChatClientAgentOptions
            {
                Id = "verify",
                Name = "Verify",
                ChatOptions = new() { Instructions = "You judge atomic claims against cited chunks." },
            });

    // Escalation is a deterministic formatter in the real build — stub stands in
    // as an LLM-shaped participant until the wrapper mechanism is proven.
    AIAgent escalation = ScriptedClientFactory.Create("escalation",
            new ScriptedTurn(Text: "✗ escalate — missing: SLA uptime percentage"))
        .AsAIAgent(new ChatClientAgentOptions
        {
            Id = "escalation",
            Name = "Escalation",
            ChatOptions = new() { Instructions = "You format the missing[] payload." },
        });

    return AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)
        .WithHandoff(triage, draft, handoffReason: "Route the question to the Draft agent")
        .WithHandoff(draft, verify, handoffReason: "Check the draft's claims")
        .WithHandoff(verify, escalation, handoffReason: "Unsupported claims — escalate")
        .WithName("stub-handoff")
        .WithDescription("Stub cs-agent handoff workflow: Triage → Draft → Verify → Escalation")
        .Build();
}

if (mode == "devui")
{
    var builder = WebApplication.CreateBuilder(args);
    var workflow = BuildWorkflow(verifyEscalates: true);
    builder.Services.AddSingleton(workflow);
    builder.AddWorkflow("stub-handoff", (sp, key) => sp.GetRequiredService<Workflow>());
    builder.AddDevUI();
    builder.Services.AddOpenAIResponses();
    builder.Services.AddOpenAIConversations();
    var app = builder.Build();
    app.MapOpenAIResponses();
    app.MapOpenAIConversations();
    app.MapDevUI();
    Console.WriteLine("devui mode: serving /devui");
    app.Run();
    return;
}

// CLI modes — one run per invocation.
var wf = BuildWorkflow(
    verifyEscalates: mode == "escalate",
    triageUngated: mode == "triage-ungated");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

Console.WriteLine($"=== scenario: {mode} ===");
await using var run = await InProcessExecution.OpenStreamingAsync(wf, cancellationToken: cts.Token);
await run.TrySendMessageAsync(new ChatMessage(ChatRole.User, "How do I rotate the API key?"));
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

var outputSeen = false;
await foreach (var evt in run.WatchStreamAsync(cts.Token))
{
    switch (evt)
    {
        case ExecutorInvokedEvent inv:
            Console.WriteLine($"  → executor: {inv.ExecutorId}");
            break;
        case AgentResponseUpdateEvent u when !string.IsNullOrEmpty(u.Update.Text):
            Console.WriteLine($"  [{u.Update.AuthorName ?? "?"}] {u.Update.Text}");
            break;
        case WorkflowOutputEvent o:
            outputSeen = true;
            Console.WriteLine($"  OUTPUT: {o.Data}");
            break;
        case WorkflowErrorEvent err:
            Console.WriteLine($"  ERROR: {err.Exception.GetType().Name}: {err.Exception.Message}");
            break;
        case WorkflowWarningEvent warn:
            Console.WriteLine($"  warn: {warn.Data}");
            break;
    }
}

Console.WriteLine(outputSeen ? "run ended with output event" : "run ended WITHOUT output event");