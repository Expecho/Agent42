using System.Diagnostics.CodeAnalysis;

namespace Agent42.Agents;

/// <summary>
/// The narrow port onto the conversation's artifact records (ADR 0084, plan cc-12 / ADR 0150):
/// <see cref="AgentGrainState.Artifacts"/>. The artifact tools read and add records through it and never hold the
/// dictionary. A version append or a title change mutates the record it hands back, so the port needs no more.
///
/// <para>It reads state through a <c>Func</c> on every call, never a captured dictionary (ADR 0148): <c>ClearStateAsync</c>
/// and an edit-fork replace it. Other grain code (the pane, purge, fork) still reads or replaces the dictionary directly,
/// which is fine for the same reason. Built with <c>new</c> in <c>OnActivateAsync</c> (ADR 0147).</para>
/// </summary>
public sealed class ArtifactList(Func<AgentGrainState> state)
{
    /// <summary>How many artifacts the conversation holds.</summary>
    public int Count => state().Artifacts.Count;

    /// <summary>The record for an id, or false for an unknown (or null) id.</summary>
    public bool TryGet(string? artifactId, [NotNullWhen(true)] out ArtifactRecord? record)
        => state().Artifacts.TryGetValue(artifactId ?? "", out record);

    /// <summary>Adds a new record under its id.</summary>
    public void Add(ArtifactRecord record) => state().Artifacts[record.Id] = record;
}
