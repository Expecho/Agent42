using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Agent42.Tests;

/// <summary>AC9: the Postgres gateway lease is exclusive, hands over after a release, and reports a lost connection.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatGatewayLeaseTests(PostgresFixture postgres)
{
    private static PostgresChatGatewayLease Lease(NpgsqlDataSource source)
        => new(source, TimeProvider.System, NullLogger<PostgresChatGatewayLease>.Instance) { RetryInterval = TimeSpan.FromMilliseconds(50) };

    private async Task<NpgsqlDataSource> SourceAsync() => new NpgsqlDataSourceBuilder(await postgres.CreateDatabaseAsync()).Build();

    [DockerFact]
    public async Task ASecondContender_IsBlocked_UntilTheHolderReleases()
    {
        await using var source = await SourceAsync();
        var first = Lease(source);
        var second = Lease(source);

        await first.AcquireAsync(CancellationToken.None);
        Assert.True(await first.RenewAsync(CancellationToken.None));

        var waiting = second.AcquireAsync(CancellationToken.None);
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted, "the second instance must not hold the lease while the first does");

        await first.ReleaseAsync(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await second.RenewAsync(CancellationToken.None));
        await second.ReleaseAsync(CancellationToken.None);
    }

    [DockerFact]
    public async Task AfterARelease_TheLeaseCanBeTakenAgain_ByTheSameInstance()
    {
        await using var source = await SourceAsync();
        var lease = Lease(source);

        await lease.AcquireAsync(CancellationToken.None);
        await lease.ReleaseAsync(CancellationToken.None);
        Assert.False(await lease.RenewAsync(CancellationToken.None));

        await lease.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await lease.RenewAsync(CancellationToken.None));
        await lease.ReleaseAsync(CancellationToken.None);
    }

    [DockerFact]
    public async Task ALostConnection_MakesRenewReturnFalse_AndTheNextInstanceTakesOver()
    {
        await using var source = await SourceAsync();
        var holder = Lease(source);
        await holder.AcquireAsync(CancellationToken.None);

        // Kill the holder's session from outside, as a network drop or a database restart would.
        await using (var admin = await source.OpenConnectionAsync())
        await using (var kill = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND granted AND pid <> pg_backend_pid()", admin))
        {
            await kill.ExecuteNonQueryAsync();
        }

        Assert.False(await holder.RenewAsync(CancellationToken.None));

        var next = Lease(source);
        await next.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await next.RenewAsync(CancellationToken.None));
        await next.ReleaseAsync(CancellationToken.None);
    }

    [DockerFact]
    public async Task ReleasedLock_IsNotLeftOnAPooledConnection()
    {
        await using var source = await SourceAsync();
        var first = Lease(source);
        await first.AcquireAsync(CancellationToken.None);
        await first.ReleaseAsync(CancellationToken.None);

        // Every pooled connection must be free of the lock: a fresh contender on the same pool gets it at once.
        var second = Lease(source);
        await second.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await second.ReleaseAsync(CancellationToken.None);
    }

    [DockerFact]
    public async Task TheContract_ConstantsAreTheBlobLeasesShape()
    {
        await using var source = await SourceAsync();
        var lease = Lease(source);

        Assert.True(lease.RenewInterval < lease.Duration);
        Assert.False(await lease.RenewAsync(CancellationToken.None), "renewing a lease never acquired reports it lost");
    }
}
