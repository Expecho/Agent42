namespace Agent42.Tests;

public sealed class TestWaitTests
{
    [Fact]
    public async Task WaitUntilAsync_ReturnsEarly()
    {
        var calls = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await TestWait.WaitUntilAsync(() => ++calls >= 3, "three polls");

        Assert.Equal(3, calls);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WaitUntilAsync_TimesOut_NamingWhatItWaitedFor()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => TestWait.WaitUntilAsync(() => false, "the moon to rise", TimeSpan.FromMilliseconds(50)));

        Assert.Contains("the moon to rise", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitUntilAsync_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        var wait = TestWait.WaitUntilAsync(() => false, "never", TimeSpan.FromMinutes(1), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitForAsync_ReturnsTheValueThatSatisfiedTheCondition()
    {
        var n = 0;

        var got = await TestWait.WaitForAsync(() => Task.FromResult(++n), v => v >= 4, "a counter to reach 4");

        Assert.Equal(4, got);
    }
}
