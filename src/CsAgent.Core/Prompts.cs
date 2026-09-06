using System.Reflection;

namespace CsAgent.Core;

/// <summary>
/// Prompt artifacts are files, authored once and loaded from embedded resources —
/// the verifier prompt is ONE file with two labeled sections (one model call).
/// </summary>
public static class Prompts
{
    public static string DraftInstructions() => Load("draft-instructions.md");
    public static string VerifyPrompt() => Load("verify-prompt.md");

    private static string Load(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith(name, StringComparison.Ordinal));
        if (resource is null)
            throw new CsAgentException(new CsAgentError(
                "prompts", "prompt-missing", $"Embedded prompt '{name}' not found in {assembly.GetName().Name}."));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}