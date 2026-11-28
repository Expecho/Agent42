using Microsoft.Extensions.Logging.Abstractions;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>ADR 0153, D10: one sweeper for the durable store and the in-memory incognito twin.</summary>
public sealed class ChatTurnSweeperTests
{
    private readonly TestTimeProvider _time = new();
    private readonly ChatTurnOptions _options = new();

    private (ChatTurnStores Stores, ChatTurnSweeper Sweeper) Create()
    {
        var stores = new ChatTurnStores(new InMemoryChatTurnBlobs(_time), _options, _time);
        return (stores, new ChatTurnSweeper(stores, _options, _time, NullLogger<ChatTurnSweeper>.Instance));
    }

    [Fact]
    public async Task Sweeper_DeletesAged_KeepsRunningAndYoung()
    {
        var (stores, sweeper) = Create();
        await stores.Durable.StartAsync("u", "aged", "t1", "r");
        await stores.Incognito.StartAsync("u", "aged-incognito", "t2", "r");

        // 25 hours on: those two are past the 24 h retention. A turn started now is young, and a turn whose owner is
        // still heartbeating has a fresh meta however long it has been running.
        _time.Offset = TimeSpan.FromHours(25);
        var running = await stores.Durable.StartAsync("u", "running", "t3", "r");
        await stores.Durable.WriteMetaAsync("u", "running", running with { LastHeartbeatUtc = _time.GetUtcNow() });

        var deleted = await sweeper.SweepOnceAsync();

        Assert.True(deleted >= 6);
        Assert.Null(await stores.Durable.GetActiveAsync("u", "aged"));
        Assert.Null(await stores.Incognito.GetActiveAsync("u", "aged-incognito"));
        Assert.Equal(ChatTurnStatus.Running, (await stores.Durable.GetActiveAsync("u", "running"))!.Status);
    }

    [Fact]
    public async Task Sweeper_KeepsAYoungTurn()
    {
        var (stores, sweeper) = Create();
        await stores.Durable.StartAsync("u", "young", "t1", "r");
        _time.Offset = TimeSpan.FromHours(23);

        Assert.Equal(0, await sweeper.SweepOnceAsync());
        Assert.NotNull(await stores.Durable.GetActiveAsync("u", "young"));
    }

    [Fact]
    public async Task Sweeper_ASickStore_IsLogged_AndTheOtherStoreIsStillSwept()
    {
        var stores = new ChatTurnStores(new ThrowingBlobs(), _options, _time);
        var sweeper = new ChatTurnSweeper(stores, _options, _time, NullLogger<ChatTurnSweeper>.Instance);
        await stores.Incognito.StartAsync("u", "c", "t", "r");
        _time.Offset = TimeSpan.FromHours(25);

        var deleted = await sweeper.SweepOnceAsync();

        Assert.True(deleted > 0);
        Assert.Null(await stores.Incognito.GetActiveAsync("u", "c"));
    }

    private sealed class ThrowingBlobs : IChatTurnBlobs
    {
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new IOException("store down");

        public Task<long?> GetLengthAsync(string key, CancellationToken cancellationToken = default) => throw new IOException("store down");

        public Task<ChatTurnSlice?> GetRangeAsync(string key, long offset, CancellationToken cancellationToken = default) => throw new IOException("store down");

        public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default) => throw new IOException("store down");

        public Task CreateAsync(string key, CancellationToken cancellationToken = default) => throw new IOException("store down");

        public Task<ChatTurnAppendResult> AppendAsync(string key, byte[] content, long expectedOffset, CancellationToken cancellationToken = default)
            => throw new IOException("store down");

        public Task<IReadOnlyList<ChatTurnBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default) => throw new IOException("store down");

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new IOException("store down");
    }
}
