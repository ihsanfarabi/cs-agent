using System.Text.Json;

namespace CsAgent.Core;

/// <summary>
/// The one serializer declaration for the result contract — CLI ask --json,
/// EvalRunner result files, and the HTTP API all serialize with these
/// options, so the surfaces stay byte-identical by construction.
/// </summary>
public static class CsAgentJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new() { WriteIndented = true };
}