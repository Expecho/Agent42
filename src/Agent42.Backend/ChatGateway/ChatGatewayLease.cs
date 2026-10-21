using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

namespace Agent42.Backend;

/// <summary>
/// The exclusive right to run the chat gateway (ADR 0111). CLAUDE.md rule 12 pins one replica, but an Azure Container
/// Apps rollover still runs the old and the new revision side by side for a while. Two gateways would both poll
/// Telegram (which answers the second poller with 409 Conflict) and both hold a Discord gateway session (so every
/// message is answered twice). The gateway connects nothing until it holds this lease, and drops every session the
/// moment a renewal fails.
/// </summary>
public interface IChatGatewayLease
{
    /// <summary>How often <see cref="RenewAsync"/> must be called to keep the lease.</summary>
    TimeSpan RenewInterval { get; }

    /// <summary>How long one acquire or renewal keeps the lease. The Signal daemon's watchdog derives its window from this.</summary>
    TimeSpan Duration { get; }

    /// <summary>Waits until this instance holds the lease. Cancelled on shutdown.</summary>
    Task AcquireAsync(CancellationToken cancellationToken);

    /// <summary>Extends the lease. <c>false</c> means it was lost: stop everything and acquire again.</summary>
    Task<bool> RenewAsync(CancellationToken cancellationToken);

    /// <summary>Gives the lease up, so a waiting instance takes over at once rather than after it expires.</summary>
    Task ReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IChatGatewayLease"/> over an Azure Blob lease on <c>chat-gateway/lease</c>, in the storage account the
/// app already uses (the container is declared in <c>infra/modules/storage.bicep</c>). A 60-second lease renewed every
/// 20 seconds: a crashed holder blocks the next one for at most a minute; a graceful shutdown releases it at once.
/// </summary>
public sealed class BlobChatGatewayLease(BlobServiceClient blobs, TimeProvider timeProvider, ILogger<BlobChatGatewayLease> logger)
    : IChatGatewayLease
{
    private const string ContainerName = "chat-gateway";
    private const string BlobName = "lease";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly string _leaseId = Guid.NewGuid().ToString();
    private BlobLeaseClient? _lease;

    public TimeSpan RenewInterval => TimeSpan.FromSeconds(20);

    public TimeSpan Duration => LeaseDuration;

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(ContainerName);
        var blob = container.GetBlobClient(BlobName);
        _lease = blob.GetBlobLeaseClient(_leaseId);
        var waiting = false;

        while (true)
        {
            try
            {
                if (!await blob.ExistsAsync(cancellationToken))
                {
                    // Azurite and a fresh account have neither; production has the container from Bicep.
                    await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                    await blob.UploadAsync(BinaryData.FromString(""), overwrite: false, cancellationToken);
                }

                await _lease.AcquireAsync(LeaseDuration, cancellationToken: cancellationToken);
                logger.LogInformation("Chat gateway lease acquired.");
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Another revision holds it (or created the blob first): wait and try again.
                if (!waiting)
                {
                    logger.LogInformation("Chat gateway lease is held by another instance; waiting for it.");
                    waiting = true;
                }
            }

            await Task.Delay(RetryInterval, timeProvider, cancellationToken);
        }
    }

    public async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        if (_lease is null)
        {
            return false;
        }

        try
        {
            await _lease.RenewAsync(cancellationToken: cancellationToken);
            return true;
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Chat gateway lease renewal failed; stopping every chat session.");
            return false;
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_lease is null)
        {
            return;
        }

        try
        {
            await _lease.ReleaseAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            // Not fatal: it expires on its own within LeaseDuration.
            logger.LogWarning(ex, "Chat gateway lease release failed; it will expire on its own.");
        }
    }
}

/// <summary>A lease that is always held: DevHost, where one process is the whole world.</summary>
public sealed class NoOpChatGatewayLease : IChatGatewayLease
{
    public TimeSpan RenewInterval => TimeSpan.FromMinutes(5);

    // Never lost, so the length is only what a participant's watchdog derives its window from: a few renew intervals.
    public TimeSpan Duration => TimeSpan.FromMinutes(15);

    public Task AcquireAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
