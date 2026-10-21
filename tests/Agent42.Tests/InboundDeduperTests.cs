using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Inbound dedupe (ADR 0111): a redelivered message never runs twice, a released claim lets the platform's retry in,
/// ids are per connection, and memory is bounded in time and size.
/// </summary>
public class InboundDeduperTests
{
    [Fact]
    public void ASecondClaim_IsADuplicate_PerConnection()
    {
        var deduper = new InboundDeduper(new TestTimeProvider());

        Assert.True(deduper.TryClaim("conn-1", "42"));
        Assert.False(deduper.TryClaim("conn-1", "42"));
        Assert.True(deduper.TryClaim("conn-2", "42"));
    }

    [Fact]
    public void Release_LetsTheRetryIn()
    {
        var deduper = new InboundDeduper(new TestTimeProvider());
        deduper.TryClaim("conn-1", "42");

        deduper.Release("conn-1", "42");

        Assert.True(deduper.TryClaim("conn-1", "42"));
    }

    [Fact]
    public void AnIdIsForgottenAfterTheWindow()
    {
        var clock = new TestTimeProvider();
        var deduper = new InboundDeduper(clock);
        deduper.TryClaim("conn-1", "42");

        clock.Offset = InboundDeduper.Window;

        Assert.True(deduper.TryClaim("conn-1", "42"));
    }

    [Fact]
    public void MemoryIsBounded_OldestFirst()
    {
        var deduper = new InboundDeduper(new TestTimeProvider());
        for (var i = 0; i <= InboundDeduper.MaxEntries; i++)
        {
            Assert.True(deduper.TryClaim("conn-1", i.ToString()));
        }

        // One more claim evicts the oldest, which is then accepted again; a recent one is still a duplicate.
        Assert.True(deduper.TryClaim("conn-1", "extra"));
        Assert.True(deduper.TryClaim("conn-1", "0"));
        Assert.False(deduper.TryClaim("conn-1", InboundDeduper.MaxEntries.ToString()));
    }
}
