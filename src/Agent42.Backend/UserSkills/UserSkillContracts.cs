namespace Agent42.Backend;

/// <summary>
/// One installed skill as the browser sees it (ADR 0049). Read from the GRAIN only — never the package
/// blob and never the materialized directory, so listing a user's skills costs one grain call and works
/// even before anything has been materialized on this container.
/// </summary>
public record UserSkillResponse(
    string Id, string Name, string Description, IReadOnlyList<string> Files, DateTimeOffset UploadedAt);

/// <summary>The <c>GET /api/skills</c> payload: the user's installed skills, newest upload last.</summary>
public record UserSkillsResponse(IReadOnlyList<UserSkillResponse> Skills);

/// <summary>
/// What <c>POST /api/skills</c> returns: the skills that installed, everything that was deliberately
/// dropped, and a warning per shipped skill that an upload has SHADOWED.
///
/// <para><see cref="Skipped"/> exists because the chosen behaviour for a non-conforming zip entry is
/// "silently drop it, keep the rest" — which is only acceptable if the drop is reported. Without it a
/// user whose <c>scripts/</c> folder vanished has no way to find out that it was deliberate.</para>
///
/// <para><see cref="Warnings"/> carries the one consequence a user cannot be expected to predict: an
/// upload named after a shipped skill replaces it entirely, and the shipped <c>websearch</c> skill is the
/// only place a prompt-injection mitigation lives (ADR 0011/0049).</para>
/// </summary>
public record UserSkillUploadResponse(
    IReadOnlyList<UserSkillResponse> Skills,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Warnings);
