namespace Agent42.Agents;

/// <summary>
/// Cosine similarity over embedding vectors, shared by the two places that rank chunks in this process:
/// <see cref="InMemoryDocumentIndex"/> (the offline/dev vector store) and the in-process ranking
/// <c>read_web_document</c> does over the chunks it has just embedded (ADR 0066).
///
/// <para>Extracted rather than duplicated because the two rankings must order identically — an excerpt
/// the tool returns inline for the current turn, and the same excerpt the always-on provider retrieves
/// on the next one, should not disagree about which chunk was most relevant.</para>
/// </summary>
public static class VectorSimilarity
{
    /// <summary>
    /// Cosine similarity in [-1, 1]; higher is more similar. Mismatched lengths and zero-magnitude
    /// vectors score 0 (never rank), so a degenerate embedding cannot poison the ordering.
    /// </summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
