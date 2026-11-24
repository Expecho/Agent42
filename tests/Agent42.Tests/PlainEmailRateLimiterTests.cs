using Agent42.Backend;

namespace Agent42.Tests;

public class PlainEmailRateLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Refuses_OncePerAgentCapIsReached_AndCountsAgentsSeparately()
    {
        var limiter = new PlainEmailRateLimiter(2);

        Assert.True(limiter.TryAcquire("a", T0));
        Assert.True(limiter.TryAcquire("a", T0.AddMinutes(1)));
        Assert.False(limiter.TryAcquire("a", T0.AddMinutes(2)));
        Assert.True(limiter.TryAcquire("b", T0.AddMinutes(2)));
    }

    [Fact]
    public void SlidingWindow_FreesASlotAfterAnHour()
    {
        var limiter = new PlainEmailRateLimiter(1);
        Assert.True(limiter.TryAcquire("a", T0));
        Assert.False(limiter.TryAcquire("a", T0.AddMinutes(59)));

        Assert.True(limiter.TryAcquire("a", T0.AddMinutes(61)));
    }

    [Fact]
    public void LimitZero_IsUnlimited()
    {
        var limiter = new PlainEmailRateLimiter(0);

        Assert.All(Enumerable.Range(0, 500), _ => Assert.True(limiter.TryAcquire("a", T0)));
    }
}
