namespace Agent42.Agents;

/// <summary>
/// Bounds on how much history a WINDOWED conversation keeps, in the tradition of
/// <see cref="ChatSearchLimits"/> and <see cref="WebToolLimits"/> — but for the oldest reason in
/// this repo: the whole session is re-serialized into one 2 MB <c>agent-state</c> item every turn
/// (ADR 0004), and a continuous agent funnels every chat into one grain that never rotates.
///
/// <para><b>Two id shapes are windowed, not one.</b> ADR 0016's continuous chats
/// (<see cref="ContinuousConversationIdPrefix"/>) were the original case, and ADR 0070's MCP
/// conversations (<c>AgentMcpLimits.ConversationIdPrefix</c>) were admitted for a sharper version of
/// the same problem: an external caller reusing one id has no sidebar row and no reason to ever start
/// a fresh conversation, so nothing else bounds that session. Ordinary browser chats are deliberately
/// excluded — see the per-message byte cost in <c>AgentGrain.ApplyRetention</c>.</para>
///
/// <para><b>These bound AGE, not SIZE, so they narrow ADR 0016's failure mode without closing
/// it.</b> A busy continuous agent — or one oversized tool return, which is persisted verbatim —
/// can still exceed 2 MB inside the window. Delete-to-reset remains the escape hatch. Anyone
/// reading this as "the brick is solved" is reading it wrong.</para>
///
/// <para>Every value here is a fixed constant on purpose: not per-agent, not per-user, not
/// operator config. Retention that varies per conversation is a durability contract the UI would
/// have to explain, and there is no UI for this at all.</para>
/// </summary>
public static class RetentionLimits
{
    /// <summary>
    /// How far back a continuous chat keeps its history. Turns older than this are <b>hard
    /// deleted</b> from the session on the next persisted turn — not trimmed from what is sent to
    /// the model, because only deletion reclaims the 2 MB budget.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// Turns that always survive, however old. Pruning must never empty a session: an agent whose
    /// entire history expired would otherwise be handed a conversation with no user turn at all.
    ///
    /// <para><b>Honest about what this bound is.</b> Through the shipped paths it never actually
    /// binds — a prune only ever runs from a persist site, and the turn being persisted was stamped
    /// moments earlier, so the newest turn is always inside the window and the leading-run walk stops
    /// before it. This is the belt to that braces: it keeps the invariant a property of the code
    /// rather than of an argument about call sites, which is what a future fourth persist site (or a
    /// clock that jumps) would otherwise quietly break.</para>
    /// </summary>
    public const int MinimumPreservedTurns = 1;

    /// <summary>
    /// A message whose <c>CreatedAt</c> is older than this is treated as unstamped and restamped
    /// with "now" rather than believed.
    ///
    /// <para><b>This is a safety clamp on a DELETION decision, not tidiness.</b> Nothing in this
    /// repo or in Agent Framework stamps <c>CreatedAt</c> on the streaming chat path — the
    /// coalescer drops even a provider-supplied value — but on the <c>RunAsync</c> reminder path a
    /// provider-set value <i>is</i> preserved into the session. A provider stamping
    /// <c>DateTimeOffset.UnixEpoch</c> (or a default zero) would otherwise mark a live turn
    /// instantly expired and delete it on the next persist: silently, and in production only.</para>
    ///
    /// <para>A provider setting a <i>plausible but wrong</i> past value still wins. No provider has
    /// been observed doing that; it is recorded as a residual rather than pretended closed.</para>
    /// </summary>
    public static readonly TimeSpan MaxPlausibleAge = TimeSpan.FromDays(365);

    /// <summary>
    /// The conversation-id prefix that marks a continuous agent chat (ADR 0016). The grain has no
    /// other server-side signal: <c>Continuous</c> lives on <c>AgentDefinition</c> alone and is
    /// deliberately never snapshotted into <c>AgentBinding</c> or grain state, because it is
    /// routing rather than persona.
    ///
    /// <para>Safe as a discriminator because the client mints a per-chat id with
    /// <c>crypto.randomUUID()</c>, which cannot produce this prefix — a property
    /// <c>wwwroot/js/agents.js</c> already relies on and comments.</para>
    /// </summary>
    public const string ContinuousConversationIdPrefix = "agent-";

    /// <summary>The per-agent retention presets in minutes (ADR 0140): 1 hour, 24 hours, 7 days, 30 days.</summary>
    public static readonly IReadOnlyList<int> Presets = [60, 1440, 10080, 43200];

    /// <summary>
    /// The reserved Orleans reminder name of the idle retention sweep (ADR 0140). Model-chosen reminder names
    /// (<c>schedule_prompt</c>) may never start with this prefix, and <c>cancel_all_scheduled_prompts</c> skips it.
    /// </summary>
    public const string SweepReminderName = "__retention";

    /// <summary>The shortest sweep period; Orleans reminders fire at most once a minute.</summary>
    public static readonly TimeSpan MinSweepPeriod = TimeSpan.FromMinutes(15);

    /// <summary>The longest sweep period.</summary>
    public static readonly TimeSpan MaxSweepPeriod = TimeSpan.FromHours(6);

    /// <summary>How often the idle sweep runs for a window: <c>Clamp(window / 4, 15 min, 6 h)</c> (ADR 0140).</summary>
    public static TimeSpan SweepPeriod(TimeSpan window)
    {
        var quarter = window / 4;
        return quarter < MinSweepPeriod ? MinSweepPeriod : quarter > MaxSweepPeriod ? MaxSweepPeriod : quarter;
    }
}
