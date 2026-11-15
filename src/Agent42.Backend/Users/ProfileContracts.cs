

namespace Agent42.Backend;

// Client payload for PUT /api/profile. Every field is nullable-absent because the handler does
// read-modify-write with absent-means-keep (ADR 0021 + ADR 0005): PreferredLanguage is the BCP-47 tag
// ("" = device default, null = keep stored); Theme is "system"|"light"|"dark" (null = keep stored);
// EnabledTools is the user's default selectable-tools set (list of ToolCatalog keys, null = keep stored).
// SuggestFollowUps toggles the follow-up suggestion prompts (null = keep stored, true/false set it).
// Palette is a ThemePalettes id (null = keep stored, unknown ids clamp to amber in the grain).
// The UI never sends null to mean "reset".
public record ProfileUpdateRequest(
    string? PreferredLanguage = null, string? Theme = null,
    IReadOnlyList<string>? EnabledTools = null, bool? SuggestFollowUps = null, string? Palette = null);
