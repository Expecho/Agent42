
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// Fully-populated instances of every persisted grain-state root, shared by
/// <see cref="SerializationCompatibilityTests"/> and the throwaway generator that produced its
/// committed blobs. Every member carries a non-default, distinguishable value, so a dropped or
/// renumbered <c>[Id(n)]</c> shows up as a failed assertion rather than a silently-default field.
/// </summary>
public static class GoldenStates
{
    private static readonly DateTimeOffset Created = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);

    public static AgentGrainState AgentGrain() => new()
    {
        SerializedSession = """{"messages":[{"role":"user","text":"golden"}]}""",
        ScheduledPrompts = new Dictionary<string, ReminderInfo>
        {
            ["daily-standup"] = new("daily-standup", "Summarise the standup", 90.5, 1440.25, Created, 7),
        },
        DeploymentName = "foundry:gpt-5.4",
        AgentId = "agent-golden",
        AgentName = "Golden Agent",
        SystemPrompt = "You are the golden fixture.",
        TurnAttachments = [[new AttachmentRef("att-1", "user/conv/att-1.png", "image/png", "diagram.png")]],
        DocumentNames = ["handbook.pdf"],
        FetchableUrls = ["https://example.com/a"],
        GeneratedImages = new Dictionary<string, AttachmentRef>
        {
            ["msg-9"] = new("gen-1", "user/conv/gen-1.png", "image/png", "generated.png"),
        },
        EnabledTools = ["web_search", "mcp:server-1"],
    };

    public static AgentRegistryState AgentRegistry() => new()
    {
        Agents =
        [
            new AgentDefinition(
                "agent-golden", "Golden", "A stored persona", "foundry:gpt-5.4", "Be golden.",
                Created, Updated, Continuous: true, EnabledTools: ["web_search"], ApiKeyHash: "sha256-abc"),
        ],
    };

    public static AgentKeyDirectoryState AgentKeyDirectory() =>
        new() { UserId = "user@example.com", AgentId = "agent-golden" };

    public static ConversationIndexState ConversationIndex() => new()
    {
        Conversations = [new ConversationInfo("conv-1", "A golden chat", Updated, "agent-golden", "Golden")],
    };

    public static ShareLinkDirectoryState ShareLinkDirectory() =>
        new() { UserId = "user@example.com", ConversationId = "conv-1" };

    public static UsageRollupState UsageRollup() => new()
    {
        HourlyTokens = new Dictionary<string, TokenBucket> { ["h"] = Tokens(1_767_312_000) },
        DailyTokens = new Dictionary<string, TokenBucket> { ["d"] = Tokens(1_767_225_600) },
        HourlyTools = new Dictionary<string, ToolBucket> { ["h"] = Tools(1_767_312_000) },
        DailyTools = new Dictionary<string, ToolBucket> { ["d"] = Tools(1_767_225_600) },
    };

    private static TokenBucket Tokens(long bucket) => new()
    {
        BucketUnixSeconds = bucket,
        Application = "Golden Agent",
        Provider = "foundry",
        Model = "gpt-5.4",
        UserKey = "user@example.com",
        InputTokens = 111,
        OutputTokens = 222,
        CachedInputTokens = 333,
        ReasoningOutputTokens = 444,
        CallCount = 555,
        ErrorCount = 666,
    };

    private static ToolBucket Tools(long bucket) => new()
    {
        BucketUnixSeconds = bucket,
        Application = "Golden Agent",
        ToolName = "web_search",
        UserKey = "user@example.com",
        Count = 777,
        ErrorCount = 888,
    };

    public static UserMcpConfigState UserMcpConfig() => new()
    {
        Config = new UserMcpConfig(
            [
                new StoredMcpServer(
                    "srv-1", "Dataverse", "https://mcp.example/sse", nameof(McpAuthMode.InteractiveOAuth),
                    Enabled: true, HeaderName: "X-Api-Key", HeaderValueCiphertext: "cipher-header",
                    Authority: "https://login.example/tenant", ClientId: "client-1",
                    ClientSecretCiphertext: "cipher-secret", Scopes: ["scope.read", "scope.write"],
                    TokenCiphertext: "cipher-token"),
            ],
            Version: 3),
    };

    public static UserMcpOAuthPendingState UserMcpOAuthPending() => new()
    {
        Pending =
        [
            new McpOAuthPending(
                "state-1", "srv-1", "verifier-1", "client-1", "https://login.example/token",
                "https://chat.agent42.nl/api/mcp/oauth/callback", ["scope.read"], "https://mcp.example"),
        ],
    };

    public static UserProfileState UserProfile() => new()
    {
        Profile = new UserProfile("nl-NL", "dark", ["web_search", "location"], SuggestFollowUps: false),
    };

    public static UserProviderConfigState UserProviderConfig() => new()
    {
        Config = new UserProviderConfig(
            [
                new StoredProvider(
                    "mine", "OpenAICompatible", "My provider", "https://api.example/v1", "cipher-key",
                    // The five capability bits alternate rather than all being true: five adjacent
                    // same-typed [Id]s are exactly where a renumbering swaps two values silently, and
                    // an all-true fixture could not tell a swap from a correct read.
                    [new StoredModel("m1", "Model One", SupportsTools: true, SupportsVision: false,
                        SupportsImageGeneration: true, SupportsEmbeddings: false, SupportsSpeech: true)],
                    UseResponsesApi: true),
            ],
            Default: "mine:m1",
            Version: 5,
            EmbeddingDefault: "mine:m1",
            SpeechDefault: "mine:m1"),
    };

    public static UserPushState UserPush() => new()
    {
        Subscriptions = [new PushSubscription("https://fcm.example/endpoint", "p256dh-key", "auth-secret")],
    };

    public static UserShareLinksState UserShareLinks() => new()
    {
        Links = [new ShareLink("conv-1", "shr_goldentoken", Created)],
    };

    public static UserSkillsState UserSkills() => new()
    {
        Config = new UserSkillConfig(
            [
                new StoredUserSkill(
                    "skill-1", "mynotes", "Takes notes.",
                    ["SKILL.md", "references/notes.md"],
                    Created),
            ],
            Version: 4),
    };
}
