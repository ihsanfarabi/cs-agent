using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MafStubHandoff;

/// <summary>One scripted model response: text and/or a handoff tool call.</summary>
public sealed record ScriptedTurn(string? Text = null, string? HandoffTo = null, string? HandoffReason = null);

/// <summary>
/// Fake IChatClient (MAF test-fake pattern): emits scripted turns in order.
/// Handoff = a FunctionCallContent for the synthetic tool the handoff builder
/// injects — name "handoff_to_{agentId}". No network, no key.
/// </summary>
public sealed class ScriptedChatClient(string agentId, params ScriptedTurn[] turns) : IChatClient
{
    private int _turnIndex;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var turn = NextTurn(options);
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(turn.Text))
            contents.Add(new TextContent(turn.Text));
        if (turn.HandoffTo is not null)
            contents.Add(new FunctionCallContent(
                callId: $"call-{agentId}-{_turnIndex}",
                name: ResolveHandoffToolName(options, turn.HandoffTo),
                arguments: new Dictionary<string, object?> { ["reason"] = turn.HandoffReason ?? "scripted handoff" }));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. contents])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = NextTurn(options);
        var update = new ChatResponseUpdate();
        update.Role = ChatRole.Assistant;
        update.AuthorName = agentId;
        if (!string.IsNullOrEmpty(turn.Text))
            update.Contents.Add(new TextContent(turn.Text));
        if (turn.HandoffTo is not null)
            update.Contents.Add(new FunctionCallContent(
                callId: $"call-{agentId}-{_turnIndex}",
                name: ResolveHandoffToolName(options, turn.HandoffTo),
                arguments: new Dictionary<string, object?> { ["reason"] = turn.HandoffReason ?? "scripted handoff" }));
        yield return update;
        await Task.CompletedTask;
    }

    /// <summary>
    /// MAF names the injected handoff tools itself — never guess the name;
    /// read the tool matching the requested target from ChatOptions.Tools.
    /// </summary>
    private string ResolveHandoffToolName(ChatOptions? options, string targetId)
    {
        var wanted = $"handoff_to_{targetId}";
        var tools = options?.Tools;
        if (tools is not null)
        {
            var exact = tools.Select(t => t.Name).FirstOrDefault(n => n == wanted);
            if (exact is not null) return exact;
            var any = tools.Select(t => t.Name).FirstOrDefault(n => n?.StartsWith("handoff_to_", StringComparison.Ordinal) == true);
            if (any is not null)
            {
                System.Diagnostics.Debug.WriteLine($"[{agentId}] no tool '{wanted}'; using '{any}'");
                return any;
            }
        }
        System.Diagnostics.Debug.WriteLine($"[{agentId}] no handoff tools injected! target '{targetId}'");
        return wanted;
    }

    private ScriptedTurn NextTurn(ChatOptions? options)
    {
        // Surface the injected handoff tools so MAF's tool validation can match names.
        if (turns.Length == 0)
            throw new InvalidOperationException($"agent '{agentId}' has no scripted turn left");
        if (options?.Tools is { Count: > 0 } tools)
            System.Diagnostics.Debug.WriteLine(
                $"[{agentId}] injected tools: {string.Join(", ", tools.Select(t => t.GetType().Name))}");
        return turns[Math.Min(_turnIndex++, turns.Length - 1)];
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public static class ScriptedClientFactory
{
    public static IChatClient Create(string agentId, params ScriptedTurn[] turns) =>
        new ScriptedChatClient(agentId, turns);
}