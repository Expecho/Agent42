namespace Agent42.Tests;

/// <summary>
/// A controllable clock for the retention tests (ADR 0016's 24-hour window). Without one there is
/// no way to produce a turn "older than the window" — the grain stamps every message as it
/// persists it — so the prune would ship with its main branch unreachable by any test.
///
/// <para><b>It shifts the wall clock ONLY.</b> <see cref="GetTimestamp"/>,
/// <see cref="CreateTimer"/> and the rest delegate to <see cref="TimeProvider.System"/> untouched,
/// because this is registered in the SILO container and Orleans resolves <see cref="TimeProvider"/>
/// from there for its own timer machinery. A frozen or fast-forwarded monotonic clock would be
/// changing the runtime under every test in the class to serve one of them.</para>
///
/// <para><b>What that leaves unmitigated, stated rather than assumed away:</b> Orleans still sees the
/// skewed <see cref="GetUtcNow"/>, and a +25 h jump is well past the activation collector's default
/// 2 h <c>CollectionAge</c>. If it collected the grain mid-test, the activation-scoped
/// <c>_ephemeralOrdinals</c> and <c>_searchReconcilePending</c> would reset and the retention tests
/// would pass VACUOUSLY. Measured that it does not: both
/// <c>Prune_DoesNotIndexAnEphemeralTurn_AfterOrdinalsShift</c> and
/// <c>Prune_RetriesTheSearchReconcile_AfterAFailedDelete</c> fail when their production code is
/// mutated, which they could only do if that activation-scoped state survived the jump. Re-check that
/// reasoning before relying on a clock jump for anything else — a test that merely PASSES here has
/// proved nothing about the activation.</para>
///
/// <para>Deliberately hand-rolled rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>: a
/// five-line double is not worth a new package on the dependency gates.</para>
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    /// <summary>
    /// How far ahead of real time <see cref="GetUtcNow"/> reads. Zero for every test that has not
    /// asked otherwise — <c>SharedClusterFixture.ResetSiloState</c> restores it between tests,
    /// since the cluster (and so this singleton) outlives them.
    /// </summary>
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;

    public override long GetTimestamp() => System.GetTimestamp();

    public override long TimestampFrequency => System.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => System.CreateTimer(callback, state, dueTime, period);
}
