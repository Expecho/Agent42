namespace Agent42.Agents;

/// <summary>
/// A user's durable, cross-device preferences (the grain key is the user id). The first
/// user-level scalar-preferences store in the app, distinct from per-conversation and
/// per-agent state. Kept in the user's <see cref="IUserProfileGrain"/>.
///
/// Designed for extension: add new preference fields with the next <c>[Id(n)]</c> and
/// never renumber existing ones (Orleans serialization is positional). The empty default
/// (<see cref="Empty"/>) represents a user who has never saved a profile.
/// </summary>
/// <remarks>
/// <see cref="EnabledTools"/> is the user's default selectable-tools set (ADR 0021): a list of
/// <see cref="ToolCatalog"/> keys applied to every new regular chat, or <c>null</c> for "all selectable
/// tools" (the default, so a profile saved before it — and <see cref="Empty"/> — keeps every tool with no
/// migration). It is read once on <c>AgentGrain</c> activation and is only a <i>default</i>: a
/// per-conversation override beats it.
/// </remarks>
[GenerateSerializer]
[Alias("Agent42.Agents.UserProfile")]
public sealed record UserProfile(
    [property: Id(0)] string PreferredLanguage,
    // UI theme choice: "system" | "light" | "dark". Client-side render concern, but stored
    // server-side per ADR 0005 so it follows the user across devices; localStorage is only a
    // pre-paint cache. Legacy state (saved before this field) deserializes Theme as null —
    // the grain and the client both treat null/blank/unknown as "system".
    [property: Id(1)] string Theme = "system",
    [property: Id(2)] IReadOnlyList<string>? EnabledTools = null)
{
    /// <summary>
    /// The canonical default profile: no preferences set yet. Returns a fresh instance on
    /// every access — never a shared static — because Orleans' generated copier/serializer
    /// can write into a grain-state instance, and a shared static default would leak that
    /// mutation across every grain that started from it. <see cref="EnabledTools"/> defaults to
    /// null (all selectable tools).
    /// </summary>
    public static UserProfile Empty => new("", "system", null);
}
