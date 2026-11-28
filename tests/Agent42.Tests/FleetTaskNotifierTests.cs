using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>The fleet task's own pushes (ADR 0154): which final states push, the generic texts, the stuck rules, a faulty sender.</summary>
public sealed class FleetTaskNotifierTests
{
    private static FleetTaskRecord Final(string status, string? reason = null, string? origin = null)
        => new() { Id = "t1", UserId = "alice", WorkspaceId = "w1", PlannerModel = "m", Request = "SECRET REQUEST", Status = status, Reason = reason, Result = "SECRET RESULT", OriginConversationId = origin };

    private static (FleetTaskNotifier Notifier, RecordingPushNotificationSender Sender) Make()
    {
        var sender = new RecordingPushNotificationSender();
        return (new FleetTaskNotifier(sender, NullLogger.Instance), sender);
    }

    [Fact]
    public async Task Failed_PushesGenericText()
    {
        var (notifier, sender) = Make();

        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Failed, "NullReferenceException: secret path"), 0);

        var (_, message) = Assert.Single(sender.Sends);
        Assert.Equal(FleetTaskNotifier.FailedText, message.Body);
        Assert.Equal("/fleet/?task=t1", message.Url);
    }

    [Fact]
    public async Task Stopped_ByCap_Pushes_ByUser_DoesNot()
    {
        var (notifier, sender) = Make();

        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Stopped, FleetBudget.UserStopReason), 0);
        Assert.Empty(sender.Sends);

        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Stopped, "token limit reached"), 0);
        Assert.Equal(FleetTaskNotifier.LimitText, Assert.Single(sender.Sends).Message.Body);
    }

    [Fact]
    public async Task Interrupted_NeverPushes()
    {
        var (notifier, sender) = Make();

        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Interrupted, "The app shut down while this task ran."), 0);

        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task Done_AfterModelPush_Yields()
    {
        var (notifier, sender) = Make();

        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Done), 1);

        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task Failed_AfterModelPush_StillPushes()
    {
        var (notifier, sender) = Make();

        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Failed), 2);

        Assert.Single(sender.Sends);
    }

    [Theory]
    [InlineData(FleetTaskStatus.Done)]
    [InlineData(FleetTaskStatus.Failed)]
    public async Task OriginTask_NoTerminalPush(string status)
    {
        var (notifier, sender) = Make();

        await notifier.NotifyFinishedAsync(Final(status, origin: "conv-1"), 0);

        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task Texts_ContainNoTaskContent()
    {
        var (notifier, sender) = Make();

        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Done), 0);
        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Failed, "SECRET REASON"), 0);
        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Stopped, "SECRET CAP REASON"), 0);
        await notifier.NotifyStuckAsync("alice", "t1");

        Assert.Equal(4, sender.Sends.Count);
        Assert.DoesNotContain(sender.Sends, s => (s.Message.Title + s.Message.Body).Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stuck_ThrowingSender_Swallowed()
    {
        var notifier = new FleetTaskNotifier(new Throwing(), NullLogger.Instance);

        await notifier.NotifyStuckAsync("alice", "t1");
        await notifier.NotifyFinishedAsync(Final(FleetTaskStatus.Failed), 0);
    }

    [Fact]
    public void Watch_OncePerQuietSpell_ReArmsOnEvent_CappedAtTwo()
    {
        var watch = new FleetStuckWatch();
        var after = TimeSpan.FromMinutes(10);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.False(watch.ShouldFire(t0 + TimeSpan.FromMinutes(9), t0, after, false));
        Assert.True(watch.ShouldFire(t0 + TimeSpan.FromMinutes(10), t0, after, false));
        Assert.False(watch.ShouldFire(t0 + TimeSpan.FromMinutes(30), t0, after, false));

        var t1 = t0 + TimeSpan.FromMinutes(31);
        Assert.True(watch.ShouldFire(t1 + after, t1, after, false));

        var t2 = t1 + TimeSpan.FromMinutes(20);
        Assert.False(watch.ShouldFire(t2 + after, t2, after, false));
    }

    [Fact]
    public void Watch_QuestionOpenOrDisabled_NeverFires()
    {
        var watch = new FleetStuckWatch();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.False(watch.ShouldFire(t0.AddHours(5), t0, TimeSpan.FromMinutes(10), true));
        Assert.False(watch.ShouldFire(t0.AddHours(5), t0, TimeSpan.Zero, false));
        Assert.True(watch.ShouldFire(t0.AddHours(5), t0, TimeSpan.FromMinutes(10), false));
    }

    private sealed class Throwing : IPushNotificationSender
    {
        public Task SendAsync(string userId, PushMessage message, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");
    }
}
