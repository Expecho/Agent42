using Npgsql;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IChatGatewayLease"/> over a Postgres session-level advisory lock (<c>Storage:Provider = local</c>, ADR 0127),
/// the local counterpart of <see cref="BlobChatGatewayLease"/> with the same contract.
///
/// <para>The lock belongs to the DATABASE SESSION, so it is held on one dedicated connection kept open for as long as
/// the gateway runs: if the process dies or the connection drops, Postgres releases the lock and the next instance
/// takes it, with no expiry to wait out. <see cref="RenewAsync"/> therefore asks one question, whether that session still
/// holds the lock, and answers <c>false</c> the moment it does not (the service then drops every chat session).
/// <see cref="Duration"/> and <see cref="RenewInterval"/> exist because the contract has them; nothing here expires.</para>
///
/// <para>Release unlocks BEFORE the connection returns to Npgsql's pool: a pooled connection that still held the lock would
/// keep it for whoever borrowed the connection next.</para>
/// </summary>
public sealed class PostgresChatGatewayLease(NpgsqlDataSource dataSource, TimeProvider timeProvider, ILogger<PostgresChatGatewayLease> logger)
    : IChatGatewayLease
{
    // The two int4 halves of the advisory-lock key ("AG42", "GATE"); every instance must use the same pair.
    private const int KeyHigh = 0x41473432;
    private const int KeyLow = 0x47415445;

    private NpgsqlConnection? _connection;

    /// <summary>How long a contender waits between attempts while another instance holds the lock.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RenewInterval => TimeSpan.FromSeconds(20);

    public TimeSpan Duration => TimeSpan.FromSeconds(60);

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        var waiting = false;
        while (true)
        {
            try
            {
                _connection ??= await dataSource.OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand($"SELECT pg_try_advisory_lock({KeyHigh}, {KeyLow})", _connection);
                if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                {
                    logger.LogInformation("Chat gateway lease acquired.");
                    return;
                }

                if (!waiting)
                {
                    logger.LogInformation("Chat gateway lease is held by another instance; waiting for it.");
                    waiting = true;
                }
            }
            catch (NpgsqlException ex)
            {
                // The database is unreachable or the held connection broke: drop it and try again with a fresh one.
                logger.LogWarning(ex, "Chat gateway lease attempt failed; retrying.");
                await DropConnectionAsync();
            }

            await Task.Delay(RetryInterval, timeProvider, cancellationToken);
        }
    }

    public async Task<bool> RenewAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return false;
        }

        try
        {
            // Still held by THIS session? A dead connection throws; a lock somehow gone reads false.
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND granted AND pid = pg_backend_pid() " +
                $"AND classid = {KeyHigh}::oid AND objid = {KeyLow}::oid AND objsubid = 2)", _connection);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                return true;
            }

            logger.LogWarning("Chat gateway lease is no longer held; stopping every chat session.");
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning(ex, "Chat gateway lease renewal failed; stopping every chat session.");
        }

        await DropConnectionAsync();
        return false;
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({KeyHigh}, {KeyLow})", _connection);
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            // Not fatal: closing the session releases the lock anyway.
            logger.LogWarning(ex, "Chat gateway lease release failed; closing the session releases it.");
        }

        await DropConnectionAsync();
    }

    private async Task DropConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }
}
