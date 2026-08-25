namespace Agent42.Agents;

/// <summary>
/// The budget the agent-skills tools' <b>return values</b> must fit in (mirrors <see cref="WebToolLimits"/>
/// and <see cref="McpToolLimits"/>), plus the size/shape caps an <b>uploaded</b> skill package must respect.
///
/// <para>Why the return budget: a tool's <c>FunctionResultContent</c> is <b>persisted</b>. It stays in the
/// Agent Framework chat history, and <c>AgentGrain</c> re-serializes the whole session into a single
/// <c>agent-state</c> Cosmos item every turn — an item Cosmos hard-caps at <b>2 MB</b> (ADR 0004). Before
/// uploaded skills this did not bite: the shipped <c>SKILL.md</c> files are small and carry no resources, so
/// <c>load_skill</c> returned a couple of KB and <c>read_skill_resource</c> was unreachable (it waited on an
/// approval this grain has no loop for). Uploading changes both halves — the body is user-supplied and the
/// resource read is now auto-approved — so an unbudgeted return would sit in history forever, be re-sent to
/// the model every turn, and eventually make <c>WriteStateAsync</c> fail <i>after</i> the model had already
/// answered, bricking the conversation in production only.</para>
///
/// <para>The numbers are not hypothetical. Measured against Microsoft.Agents.AI 1.13.0: a 300 KB
/// <c>SKILL.md</c> made <c>load_skill</c> return <b>300,087 characters</b> (the whole body plus an
/// <c>&lt;available_resources&gt;</c> listing), and a 200 KB resource file made <c>read_skill_resource</c>
/// return all <b>200,000</b>. Both go straight into the session.</para>
///
/// <para>Anything added to a skills tool's return payload must be counted against this budget.</para>
/// </summary>
public static class SkillToolLimits
{
    /// <summary>
    /// Maximum length, in characters, of what <c>load_skill</c> hands back for one skill — the SKILL.md body
    /// plus the framework's resource/script listing. Same order of magnitude as
    /// <see cref="WebToolLimits.MaxPageTextLength"/>: a skill's guidance is prose meant to be read in one
    /// go, and anything past this is a sign the skill should have used a resource file.
    /// </summary>
    public const int MaxSkillBodyLength = 16 * 1024;

    /// <summary>
    /// Maximum length, in characters, of what <c>read_skill_resource</c> hands back for one resource. Lower
    /// than <see cref="MaxSkillBodyLength"/> because a model may read several resources in one turn, each
    /// one persisted, while <c>load_skill</c> is called once per skill.
    /// </summary>
    public const int MaxResourceTextLength = 8 * 1024;

    /// <summary>Appended to a skills tool's result when it was cut off at one of the caps above.</summary>
    public const string TruncationMarker = "\n\n[truncated]";

    // ---- upload/normalization caps -------------------------------------------------------------------
    // These bound what reaches disk, not what reaches the model. Per-upload bytes are already capped by the
    // endpoint (MaxAttachmentBytes, 10 MB); these cap what that 10 MB may EXPAND into and how much a user
    // may accumulate across uploads, since the materialized tree lives on the container filesystem.

    /// <summary>
    /// Maximum number of entries a single uploaded zip may contain. A cap on entry <i>count</i> is separate
    /// from a cap on bytes: a zip of a million empty files is small on the wire and ruinous on disk.
    /// </summary>
    public const int MaxZipEntries = 500;

    /// <summary>
    /// Maximum uncompressed bytes for one file inside an uploaded zip. Enforced against the bytes
    /// <b>actually copied</b>, never <c>ZipArchiveEntry.Length</c> — that field is attacker-controlled
    /// metadata and a zip bomb lies about it.
    /// </summary>
    public const long MaxEntryBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Maximum total uncompressed bytes across every entry of one uploaded zip. The real zip-bomb bound:
    /// a 10 MB upload must not become gigabytes on disk.
    /// </summary>
    public const long MaxTotalUncompressedBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Maximum number of skills one user may have installed at once. Bounds per-user disk by more than the
    /// per-upload limit, which otherwise bounds only a single request.
    /// </summary>
    public const int MaxInstalledSkillsPerUser = 25;

    /// <summary>
    /// Maximum number of files (SKILL.md plus resources) kept for one skill.
    ///
    /// <para><b>This is a GRAIN-STATE budget, not a disk one, and it is the layer the other caps miss.</b>
    /// Every kept path is persisted verbatim in <c>StoredUserSkill.Files</c>, and the user-skills overlay
    /// is one Cosmos item under the same 2 MB cap as the session (ADR 0004). Without this,
    /// <see cref="MaxZipEntries"/> × <see cref="MaxInstalledSkillsPerUser"/> is ~12,500 persisted paths —
    /// megabytes of pure path text — and the failure mode is the ugly one: <c>UserSkillsGrain</c> mutates
    /// its in-memory state before <c>WriteStateAsync</c>, so a rejected write leaves the activation
    /// reporting a skill that is not durable, and every later upload fails until it reactivates.</para>
    ///
    /// <para>Generous for a real skill bundle — the framework lists every resource in
    /// <c>load_skill</c>'s output, so a skill with hundreds of them is unusable long before it is
    /// unstorable.</para>
    /// </summary>
    public const int MaxFilesPerSkill = 100;
}
