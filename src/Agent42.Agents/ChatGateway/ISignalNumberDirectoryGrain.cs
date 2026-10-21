namespace Agent42.Agents;

/// <summary>
/// A global, cross-user claim on one Signal number (ADR 0111, phase 3), keyed by the normalised E.164 number. The
/// signal-cli daemon's account namespace is shared by every user, so without a claim user B could run <c>register</c>
/// on user A's number: B could not verify (the code goes to A's phone), but the call would reset A's registration.
///
/// <para>Its own grain type, never a second use of another directory (gotcha "Each credential namespace gets its own
/// directory grain type"). A <see cref="SignalNumberClaimState.Pending"/> claim expires after
/// <see cref="ChatGatewayLimits.SignalPendingClaimLifetime"/>, so an abandoned or failed registration can't squat a number
/// forever. The number is the grain key in clear: a hash of a phone number is brute-forceable, so it would hide nothing.</para>
///
/// <para>Times are passed in by the caller, so expiry is testable without a clock in the grain.</para>
/// </summary>
[Alias("Agent42.Agents.ISignalNumberDirectoryGrain")]
public interface ISignalNumberDirectoryGrain : IGrainWithStringKey
{
    /// <summary>
    /// Claims the number for <paramref name="userId"/>/<paramref name="connectionId"/> as pending. True when it was free,
    /// already this connection's, or a pending claim that expired; false when another connection holds it.
    /// </summary>
    [Alias("TryClaimAsync")]
    Task<bool> TryClaimAsync(string userId, string connectionId, DateTimeOffset now);

    /// <summary>Marks this connection's claim active (after <c>verify</c>). False when the claim isn't this connection's.</summary>
    [Alias("ActivateAsync")]
    Task<bool> ActivateAsync(string connectionId);

    /// <summary>Releases the claim, but only if it is <paramref name="connectionId"/>'s. Idempotent.</summary>
    [Alias("ReleaseAsync")]
    Task ReleaseAsync(string connectionId);

    /// <summary>Who holds the number, or <c>null</c>.</summary>
    [Alias("GetAsync")]
    Task<SignalNumberClaim?> GetAsync();
}

/// <summary>Whether a claimed number is still being registered or belongs to a working connection.</summary>
public enum SignalNumberClaimState
{
    Pending,
    Active,
}

/// <summary>One number's holder.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.SignalNumberClaim")]
public sealed record SignalNumberClaim(
    [property: Id(0)] string UserId,
    [property: Id(1)] string ConnectionId,
    [property: Id(2)] SignalNumberClaimState State,
    [property: Id(3)] DateTimeOffset ClaimedAt);
