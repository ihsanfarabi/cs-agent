using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsAgent.Core;
using ModelContextProtocol.Server;

namespace CsAgent.Mcp;

/// <summary>
/// MCP surface of the ONE result object: fields exposed as returned, never a
/// second copy built by this surface. Errors come back as structured error
/// objects, never stack traces.
/// </summary>
[McpServerToolType]
public static class CsAgentTools
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [McpServerTool]
    [Description("Answer a support question from the ingested docs. Returns the verdict "
        + "(resolved or escalate) with the answer, page citations, the claim-by-claim verifier "
        + "breakdown, model calls, seconds, and estimated cost. Verdict is computed BEFORE any "
        + "output is returned — an escalated answer is never included.")]
    public static string Ask(
        CsAgentToolbox toolbox,
        [Description("The support question to answer, e.g. \"How do I rotate my API key?\"")]
        string question)
    {
        try
        {
            return JsonSerializer.Serialize(toolbox.Ask(question), Json);
        }
        catch (CsAgentException ex)
        {
            return ErrorJson(ex);
        }
    }

    [McpServerTool]
    [Description("Ingest a directory of markdown/HTML docs into the local vector store so "
        + "the ask tool can answer questions from them. Idempotent: unchanged pages are skipped.")]
    public static string Ingest(
        CsAgentToolbox toolbox,
        [Description("Path to a directory containing .md/.html documentation files.")]
        string path)
    {
        try
        {
            return JsonSerializer.Serialize(toolbox.Ingest(path), Json);
        }
        catch (CsAgentException ex)
        {
            return ErrorJson(ex);
        }
    }

    private static string ErrorJson(CsAgentException ex) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                component = ex.Error.Component,
                code = ex.Error.Code,
                message = ex.Error.Message,
            }
        }, Json);
}