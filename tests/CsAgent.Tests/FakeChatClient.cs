using Microsoft.Extensions.AI;

namespace CsAgent.Tests;

/// <summary>
/// Fake chat client keyed by agent role: scripts the Draft and Verify responses
/// for pipeline tests — the fake-or-real model seam from the test plan.
/// </summary>
public sealed class FakeChatClient(Func<IEnumerable<ChatMessage>, string> responder) : IChatClient
{
    public int CallCount { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        var text = responder(messages);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}