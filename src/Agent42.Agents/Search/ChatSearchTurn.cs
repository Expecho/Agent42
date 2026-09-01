namespace Agent42.Agents;

/// <summary>
/// One message handed to <see cref="IChatSearchIndex.IndexTurnsAsync"/> for indexing (ADR 0061).
///
/// <para><see cref="Ordinal"/> is the index into <c>GetHistoryAsync()</c>'s projected list — never
/// a raw session-message index and never a running counter the caller keeps. It is half of the
/// document key, so it is what makes a re-index overwrite instead of duplicate.</para>
/// </summary>
public sealed record ChatSearchTurn(int Ordinal, string Role, string Text);
