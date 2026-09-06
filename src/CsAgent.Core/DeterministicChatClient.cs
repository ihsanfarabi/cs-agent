using Microsoft.Extensions.AI;

namespace CsAgent.Core;

/// <summary>
/// IChatClient wrapper that pins Temperature to 0 on every call — the verdict
/// gate must not flip on sampling variance. Wrap the provider client before
/// building agents from it.
/// </summary>
public sealed class DeterministicChatClient(IChatClient inner) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => inner.GetResponseAsync(messages, WithTemperatureZero(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => inner.GetStreamingResponseAsync(messages, WithTemperatureZero(options), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
    public void Dispose() => inner.Dispose();

    private static ChatOptions WithTemperatureZero(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.Temperature = 0f;
        return options;
    }
}