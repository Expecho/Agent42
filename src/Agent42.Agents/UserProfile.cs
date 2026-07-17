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
[GenerateSerializer]
[Alias("Agent42.Agents.UserProfile")]
public sealed record UserProfile(
    [property: Id(0)] string PreferredLanguage,
    // UI theme choice: "system" | "light" | "dark". Client-side render concern, but stored
    // server-side per ADR 0005 so it follows the user across devices; localStorage is only a
    // pre-paint cache. Legacy state (saved before this field) deserializes Theme as null —
    // the grain and the client both treat null/blank/unknown as "system".
    [property: Id(1)] string Theme = "system")
{
    /// <summary>
    /// The canonical default profile: no preferences set yet. Returns a fresh instance on
    /// every access — never a shared static — because Orleans' generated copier/serializer
    /// can write into a grain-state instance, and a shared static default would leak that
    /// mutation across every grain that started from it.
    /// </summary>
    public static UserProfile Empty => new("", "system");
}
