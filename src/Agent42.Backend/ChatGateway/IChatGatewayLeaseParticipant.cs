namespace Agent42.Backend;

/// <summary>
/// Something that lives only as long as the chat gateway holds its lease (ADR 0111): the Signal daemon, whose store
/// must never be open in two revisions at once. <see cref="ChatGatewayService"/> tells every participant about the
/// lease, concurrently, with a bounded wait per call.
///
/// <para>The contract:</para>
/// <list type="bullet">
/// <item><see cref="OnLeaseLostAsync"/> is called BEFORE the sessions stop (a hung session dispose must not delay it)
/// and before <see cref="IChatGatewayLease.ReleaseAsync"/>. It runs when a renewal failed, when the supervisor
/// failed, and on shutdown.</item>
/// <item>It may be called repeatedly, so it must be idempotent: the supervisor's stop path runs from several places.</item>
/// <item>Neither method may throw into the supervisor. The supervisor catches and logs anyway, but an implementation
/// that throws has probably not done what it was told.</item>
/// <item><see cref="OnLeaseRenewedAsync"/> follows each successful acquire or renewal, and is what a participant's own
/// watchdog counts on.</item>
/// </list>
/// </summary>
public interface IChatGatewayLeaseParticipant
{
    /// <summary>The lease was acquired or successfully renewed.</summary>
    Task OnLeaseRenewedAsync(CancellationToken cancellationToken);

    /// <summary>The lease is gone (or about to be released): stop whatever must not outlive it, and wait for it to end.</summary>
    Task OnLeaseLostAsync(CancellationToken cancellationToken);
}
