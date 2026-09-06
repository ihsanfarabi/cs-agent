namespace CsAgent.Core;

public sealed record ClaimVerdict([property: System.Text.Json.Serialization.JsonPropertyName("claim")] string Claim,
    [property: System.Text.Json.Serialization.JsonPropertyName("supported")] bool Supported,
    [property: System.Text.Json.Serialization.JsonPropertyName("supporting_chunk_ids")] int[] SupportingChunkIds,
    [property: System.Text.Json.Serialization.JsonPropertyName("unsupported_by")] string[] UnsupportedBy);

public sealed record MissingItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("claim")] string Claim,
    [property: System.Text.Json.Serialization.JsonPropertyName("note")] string Note);

/// <summary>
/// The verifier rule, pure logic — every branch:
/// all-supported → resolved; any-unsupported → escalate + missing[];
/// zero claims → escalate (never vacuously resolved);
/// malformed → retry once (caller) → fail closed to escalate.
/// </summary>
public static class VerifierRule
{
    public static bool Resolve(IReadOnlyList<ClaimVerdict> claims)
    {
        if (claims.Count == 0)
            return false; // zero claims ⇒ escalate — no vacuous resolution
        return claims.All(c => c.Supported);
    }

    public static IReadOnlyList<MissingItem> BuildMissing(
        IReadOnlyList<ClaimVerdict> claims, string question)
    {
        if (claims.Count == 0)
            return [new MissingItem(question, "no answerable claim could be extracted")];
        return [.. claims
            .Where(c => !c.Supported)
            .Select(c => new MissingItem(
                c.Claim, "no supporting chunk states this claim explicitly"))];
    }

    /// <summary>Parses verifier output; returns null on malformed (caller retries once).</summary>
    public static IReadOnlyList<ClaimVerdict>? ParseClaims(string verifierText)
    {
        if (string.IsNullOrWhiteSpace(verifierText)) return null;
        var text = verifierText.Trim();
        // tolerate code fences
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) text = text[start..end].Trim();
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var claims = new List<ClaimVerdict>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var claim = el.TryGetProperty("claim", out var c) ? c.GetString() : null;
                if (string.IsNullOrEmpty(claim)) return null;
                var supported = el.TryGetProperty("supported", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True;
                var supporting = el.TryGetProperty("supporting_chunk_ids", out var sc) && sc.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? sc.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToArray()
                    : [];
                var unsupportedBy = el.TryGetProperty("unsupported_by", out var ub) && ub.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? ub.EnumerateArray().Select(x => x.ToString()).ToArray()
                    : [];
                claims.Add(new ClaimVerdict(claim, supported, supporting, unsupportedBy));
            }
            return claims;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}