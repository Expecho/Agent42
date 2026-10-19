using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// AC8 (ADR 0106): the retention rules of ADR 0016 without a silo, on a controllable clock. The grain-level retention
/// tests in <see cref="AgentGrainTests"/> and the <c>Prune_*</c> cases in <see cref="ChatSearchIndexingTests"/> still
/// pin the persist pipeline end to end.
/// </summary>
public sealed class HistoryRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now - RetentionLimits.Window - TimeSpan.FromHours(1);
    private static readonly DateTimeOffset Fresh = Now - TimeSpan.FromHours(1);

    private readonly List<ChatMessage> _messages = [];
    private readonly List<List<AttachmentRef>> _attachments = [];
    private readonly Dictionary<string, AttachmentRef> _images = [];

    private RetentionResult Apply()
        => new HistoryRetention(new FixedClock(Now), NullLogger.Instance, "user/agent-conv")
            .Apply(new RetentionContext(_messages, _attachments, _images, () => _messages.Count(IsProjected)));

    // A user turn of one user and one assistant message, stamped at the given time.
    private void Turn(DateTimeOffset at, string text, AttachmentRef? attachment = null)
    {
        _messages.Add(new ChatMessage(ChatRole.User, text) { CreatedAt = at });
        _messages.Add(new ChatMessage(ChatRole.Assistant, text + " reply") { CreatedAt = at });
        _attachments.Add(attachment is null ? [] : [attachment]);
    }

    private static bool IsProjected(ChatMessage m) => !string.IsNullOrWhiteSpace(m.Text);

    private List<string> Texts() => [.. _messages.Select(m => m.Text)];

    [Fact]
    public void ExpiredLeadingTurns_AreRemoved_AndTheProjectedDeltaReported()
    {
        Turn(Old, "one");
        Turn(Old, "two");
        Turn(Fresh, "three");

        var result = Apply();

        Assert.Equal(new RetentionResult(Pruned: true, DroppedProjectedTurns: 4), result);
        Assert.Equal(["three", "three reply"], Texts());
    }

    [Fact]
    public void NothingExpired_PrunesNothing()
    {
        Turn(Fresh, "one");
        Turn(Fresh, "two");

        Assert.Equal(RetentionResult.None, Apply());
        Assert.Equal(4, _messages.Count);
    }

    // A leading run only: a live turn stops the walk, so an expired one behind it survives.
    [Fact]
    public void OnlyALeadingRunIsPruned()
    {
        Turn(Fresh, "one");
        Turn(Old, "two");
        Turn(Fresh, "three");

        Assert.False(Apply().Pruned);
    }

    [Fact]
    public void TheMinimumPreservedTurnsSurvive_HoweverOld()
    {
        Turn(Old, "one");
        Turn(Old, "two");

        Apply();

        Assert.Equal(["two", "two reply"], Texts());
    }

    // A turn whose reply landed inside the window keeps its prompt.
    [Fact]
    public void ATurnIsExpiredOnlyWhenEveryMessageIs()
    {
        Turn(Old, "one");
        _messages[1].CreatedAt = Fresh;
        Turn(Fresh, "two");

        Assert.False(Apply().Pruned);
    }

    [Fact]
    public void ALeadingNonUserPrefix_IsNeverTouched()
    {
        _messages.Add(new ChatMessage(ChatRole.System, "prefix") { CreatedAt = Old });
        Turn(Old, "one");
        Turn(Fresh, "two");

        Apply();

        Assert.Equal(["prefix", "two", "two reply"], Texts());
    }

    // Whole turns go, so a tool result never outlives its call (most providers reject an orphan).
    [Fact]
    public void AToolCallAndItsResult_GoTogether()
    {
        _messages.Add(new ChatMessage(ChatRole.User, "search") { CreatedAt = Old });
        _messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "web_search")]) { CreatedAt = Old });
        _messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "hits")]) { CreatedAt = Old });
        _messages.Add(new ChatMessage(ChatRole.Assistant, "found") { CreatedAt = Old });
        _attachments.Add([]);
        Turn(Fresh, "next");

        Apply();

        Assert.DoesNotContain(_messages, m => m.Contents.OfType<FunctionResultContent>().Any());
        Assert.Equal(["next", "next reply"], Texts());
    }

    [Fact]
    public void TurnAttachments_AreRealignedByTheSameCount()
    {
        var kept = new AttachmentRef("img-2", "blob/img-2", "image/png", "kept.png");
        Turn(Old, "one", new AttachmentRef("img-1", "blob/img-1", "image/png", "gone.png"));
        Turn(Fresh, "two", kept);

        Apply();

        Assert.Same(kept, Assert.Single(Assert.Single(_attachments)));
    }

    // Defensive: a list shorter than the user turns is clamped rather than throwing inside the turn.
    [Fact]
    public void AShortAttachmentList_IsClamped()
    {
        Turn(Old, "one");
        Turn(Old, "two");
        Turn(Fresh, "three");
        _attachments.Clear();

        Apply();

        Assert.Empty(_attachments);
    }

    [Fact]
    public void ADroppedGeneratedImage_LeavesTheIndex()
    {
        var image = new AttachmentRef("gen-1", "blob/gen-1", "image/png", "gen.png");
        _images["gen-1"] = image;
        _images["gen-2"] = image;
        _messages.Add(new ChatMessage(ChatRole.User, "draw") { CreatedAt = Old });
        _messages.Add(new ChatMessage(ChatRole.Assistant, [new UriContent("https://img/1", "image/png")])
        {
            CreatedAt = Old,
            AdditionalProperties = new() { ["assistantImageId"] = "gen-1" },
        });
        _attachments.Add([]);
        Turn(Fresh, "next");

        Apply();

        Assert.Equal(["gen-2"], _images.Keys);
    }

    // The stamp is a clamp: null, future and implausibly old values become "now", a believable one is kept.
    [Fact]
    public void Stamp_ReplacesOnlyUnbelievableTimestamps()
    {
        _messages.Add(new ChatMessage(ChatRole.User, "null") { CreatedAt = null });
        _messages.Add(new ChatMessage(ChatRole.Assistant, "future") { CreatedAt = Now + TimeSpan.FromDays(1) });
        _messages.Add(new ChatMessage(ChatRole.User, "epoch") { CreatedAt = DateTimeOffset.UnixEpoch });
        _messages.Add(new ChatMessage(ChatRole.Assistant, "fresh") { CreatedAt = Fresh });

        Apply();

        Assert.Equal([Now, Now, Now, Fresh], _messages.Select(m => m.CreatedAt));
    }

    // An epoch timestamp would otherwise mark a live turn expired and delete it on the next persist.
    [Fact]
    public void AnImplausiblyOldTurn_IsNotPruned()
    {
        Turn(DateTimeOffset.UnixEpoch, "one");
        Turn(Fresh, "two");

        Assert.False(Apply().Pruned);
    }

    [Fact]
    public void UserTurnIndices_ListsEveryUserMessage()
    {
        Turn(Fresh, "one");
        _messages.Insert(0, new ChatMessage(ChatRole.System, "prefix"));
        Turn(Fresh, "two");

        Assert.Equal([1, 3], HistoryRetention.UserTurnIndices(_messages));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
