using System.Diagnostics;

namespace Agent42.Tests;

/// <summary>
/// The one poll-until helper for tests (clean-code programme, plan cc-02). Tests used to carry a private copy
/// each, with deadlines from 5 s to 30 s and half of them silently returning on timeout.
///
/// <para>The deadline is generous on purpose: the suite runs thousands of tests in parallel, and a starved thread
/// pool can take seconds per step. The loop exits the instant the condition holds, so a generous bound costs
/// nothing when the test passes. It is measured with <see cref="Stopwatch"/>, not the wall clock.</para>
///
/// <para>For "nothing happened" assertions never sleep and then assert: send a following marker that must be
/// processed after the one under test, wait for <b>that</b> with this helper, then assert.</para>
/// </summary>
public static class TestWait
{
    /// <summary>The default upper bound. Only a failing test ever waits this long.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    public static Task WaitUntilAsync(
        Func<bool> condition, string what = "the condition", TimeSpan? timeout = null, CancellationToken ct = default)
        => WaitUntilAsync(() => Task.FromResult(condition()), what, timeout, ct);

    /// <summary>Polls until <paramref name="condition"/> holds; throws a <see cref="TimeoutException"/> naming <paramref name="what"/>.</summary>
    public static async Task WaitUntilAsync(
        Func<Task<bool>> condition, string what = "the condition", TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var limit = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed >= limit)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0.#}s waiting for {what}.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>Polls <paramref name="produce"/> until its result satisfies <paramref name="ready"/> and returns that result.</summary>
    public static async Task<T> WaitForAsync<T>(
        Func<Task<T>> produce, Func<T, bool> ready, string what, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var last = await produce();
        await WaitUntilAsync(async () =>
        {
            last = await produce();
            return ready(last);
        }, what, timeout, ct);
        return last;
    }
}
