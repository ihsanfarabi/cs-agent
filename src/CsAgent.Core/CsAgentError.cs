namespace CsAgent.Core;

/// <summary>
/// Structured error family: component + named var/record/file. No stack traces;
/// CLI maps these to a non-zero exit and a single-line message.
/// </summary>
public sealed record CsAgentError(
    string Component,
    string Code,
    string Message)
{
    public override string ToString() => $"{Component}: [{Code}] {Message}";
}