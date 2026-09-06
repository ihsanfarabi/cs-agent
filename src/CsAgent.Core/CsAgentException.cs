namespace CsAgent.Core;

/// <summary>Carries a structured error; never a bare message.</summary>
public sealed class CsAgentException(CsAgentError error) : Exception(error.ToString())
{
    public CsAgentError Error { get; } = error;
}