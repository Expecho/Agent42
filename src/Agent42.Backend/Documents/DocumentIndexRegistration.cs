using Agent42.Agents;
using Microsoft.Azure.Cosmos;

namespace Agent42.Backend;

/// <summary>
/// Registers the document index (ADR 0007) — the one startup decision the Documents feature owns.
///
/// <para>Lifted out of <c>Program.cs</c> by ADR 0050: the composition root is ~470 lines of
/// configuration branches and had drifted past <c>cognitive_complexity_max</c>. Extracting a
/// feature's registration into that feature's folder is the ADR 0040 shape applied to startup, and it
/// is a better answer than reshaping a branch in place to shed a point — the alternative was
/// collapsing an <c>if</c>/<c>else</c> into a ternary purely to move the number, which would have
/// bought the metric and cost the reader.</para>
/// </summary>
public static class DocumentIndexRegistration
{
    /// <summary>The vector-policy dimension Bicep fixes for <c>text-embedding-3-large</c> (ADR 0007).</summary>
    private const int DefaultDocChunkDimensions = 3072;

    /// <summary>
    /// Uses Cosmos vector search only when the doc-chunks container is configured (production,
    /// threaded from Bicep). Under the AppHost's Cosmos emulator — which cannot do vector search —
    /// that key is absent, so the in-memory index is used and the emulator never receives a vector
    /// query (constraint 6). The container is pre-created in Bicep with its vector policy;
    /// <c>CosmosDocumentIndex</c> never creates on use (<c>disableLocalAuth</c>).
    /// </summary>
    public static void AddDocumentIndex(this IServiceCollection services, IConfiguration configuration)
    {
        var docChunksContainer = configuration["Cosmos:DocChunksContainerName"];
        if (string.IsNullOrWhiteSpace(docChunksContainer))
        {
            services.AddSingleton<IDocumentIndex, InMemoryDocumentIndex>();
            return;
        }

        // Threaded through so ingest can reject a user-chosen embedding model whose vectors do not fit,
        // with a clear message instead of an opaque Cosmos error. Config-overridable only if the
        // container is ever recreated.
        var dimensions = configuration.GetValue<int?>("Cosmos:DocChunksDimensions") ?? DefaultDocChunkDimensions;
        services.AddSingleton<IDocumentIndex>(sp => new CosmosDocumentIndex(
            sp.GetRequiredService<CosmosClient>(), "agent42", docChunksContainer, dimensions));
    }
}
