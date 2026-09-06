namespace CsAgent.Core;

public static class VectorMath
{
    /// <summary>In-process cosine similarity; zero vectors score 0.</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new CsAgentException(new CsAgentError(
                "vector", "dimension-mismatch",
                $"Cosine needs equal-length vectors; got {a.Length} vs {b.Length}."));
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0f : dot / MathF.Sqrt(na * nb);
    }
}