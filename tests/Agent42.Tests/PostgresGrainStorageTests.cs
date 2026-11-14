using System.Text;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Orleans.Runtime;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// AC3 and AC4: grain state on Postgres is the same <c>$type</c> JSON the Cosmos path writes, documents written by an
/// earlier deployment still read, and the JSON-path hazards (collection expressions, <c>bool = true</c>) behave identically.
/// Runs against a real silo and a real Postgres (Testcontainers); skips, never fails, without Docker.
///
/// <para>The tests drive the registered <c>agent-state</c> storage provider directly: the provider and its serializer are
/// what the plan is about, and reaching them through a grain would only add the grain's own services.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresGrainStorageTests(PostgresFixture postgres)
{
    private static readonly GrainId Grain = GrainId.Create("test", "user-1/conv-1");

    private static async Task<IGrainStorage> StorageAsync(IHost host)
        => host.Services.GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);

    private static async Task<string> RawPayloadAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT convert_from(payloadbinary, 'UTF8') FROM orleansstorage", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ReplacePayloadAsync(string connectionString, string json)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE orleansstorage SET payloadbinary = convert_to(@json, 'UTF8')", connection);
        command.Parameters.AddWithValue("json", json);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    // Each call is a fresh GrainState (no ETag), like a newly activated grain: the provider's row version means a second
    // write for the SAME grain id needs the state object the first one returned, so tests use one grain id per write.
    private static async Task WriteAsync<T>(IGrainStorage storage, string stateName, T state, GrainId? grain = null)
        where T : new()
    {
        var grainState = new GrainState<T> { State = state };
        await storage.WriteStateAsync(stateName, grain ?? Grain, grainState);
    }

    private static async Task<T> ReadAsync<T>(IGrainStorage storage, string stateName, GrainId? grain = null)
        where T : new()
    {
        var grainState = new GrainState<T> { State = new T() };
        await storage.ReadStateAsync(stateName, grain ?? Grain, grainState);
        return grainState.State;
    }

    [DockerFact]
    public async Task GrainState_IsPersistedAsJson_WithAssemblyQualifiedTypeNames()
    {
        var cs = await postgres.CreateDatabaseAsync();
        using var host = await PostgresSilo.StartAsync(cs);
        var storage = await StorageAsync(host);

        await WriteAsync(storage, "state", GoldenStates.AgentGrain());

        var raw = await RawPayloadAsync(cs);
        Assert.Contains("\"$type\":\"Agent42.Agents.AgentGrainState, Agent42.Agents\"", raw, StringComparison.Ordinal);

        // Byte for byte what the serializer the silo resolves (the one the Cosmos path uses) writes for the same state.
        var expected = Encoding.UTF8.GetString(
            host.Services.GetRequiredService<IGrainStorageSerializer>().Serialize(GoldenStates.AgentGrain()).ToArray());
        Assert.Equal(expected, raw);
    }

    [DockerFact]
    public async Task GoldenState_RoundTripsThroughThePostgresRow()
    {
        var cs = await postgres.CreateDatabaseAsync();
        using var host = await PostgresSilo.StartAsync(cs);
        var storage = await StorageAsync(host);

        await WriteAsync(storage, "state", GoldenStates.AgentGrain());
        var restored = await ReadAsync<AgentGrainState>(storage, "state");

        Assert.Equivalent(GoldenStates.AgentGrain(), restored, strict: true);
    }

    [DockerFact]
    public async Task DocumentWrittenByAnEarlierDeployment_StillDeserializes()
    {
        var cs = await postgres.CreateDatabaseAsync();
        using var host = await PostgresSilo.StartAsync(cs);
        var storage = await StorageAsync(host);

        // Create the row through the provider (so its hash and keys are the provider's own), then put the committed
        // pre-move document in its payload, exactly as an older deployment would have left it.
        await WriteAsync(storage, "state", new AgentGrainState());
        await ReplacePayloadAsync(cs, GrainStorageSerializerTests.StoredAgentGrainStateJson);

        var restored = await ReadAsync<AgentGrainState>(storage, "state");

        Assert.Equal("foundry:gpt-5.4", restored.DeploymentName);
        Assert.Equal("agent-golden", restored.AgentId);
        Assert.Equal("Golden Agent", restored.AgentName);
        Assert.Equal(["web_search", "mcp:server-1"], restored.EnabledTools);
        Assert.Equal("daily-standup", Assert.Single(restored.ScheduledPrompts).Value.Name);
        Assert.Equal("att-1", Assert.Single(Assert.Single(restored.TurnAttachments)).Id);
    }

    [DockerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatConnection_Enabled_SurvivesThePostgresRow(bool enabled)
    {
        var cs = await postgres.CreateDatabaseAsync();
        using var host = await PostgresSilo.StartAsync(cs);
        var storage = await StorageAsync(host);

        await WriteAsync(storage, "state", new UserChatConnectionsState
        {
            Connections = [new ChatConnection { Id = "c", Platform = "Telegram", Enabled = enabled }],
        });
        var restored = await ReadAsync<UserChatConnectionsState>(storage, "state");

        Assert.Equal(enabled, Assert.Single(restored.Connections).Enabled);
    }

    [DockerFact]
    public async Task StateBuiltWithACollectionExpression_IsUnreadableOnPostgresToo_AndToListIsReadable()
    {
        var cs = await postgres.CreateDatabaseAsync();
        using var host = await PostgresSilo.StartAsync(cs);
        var storage = await StorageAsync(host);
        IReadOnlyList<string> discovered = ["alpha", "beta"];

        UserMcpConfigState StateWith(IReadOnlyList<string> names) => new()
        {
            Config = new UserMcpConfig(
                new List<StoredMcpServer>
                {
                    new("s1", "n", "https://e/mcp", nameof(McpAuthMode.None), Enabled: true, KnownToolNames: names),
                },
                1),
        };

        // The same hazard as the Cosmos path (ADR 0039, GrainStorageSerializerTests): the compiler-internal list type is
        // written into $type and cannot be resolved on read.
        await WriteAsync(storage, "state", StateWith([.. discovered]));
        var ex = await Record.ExceptionAsync(() => ReadAsync<UserMcpConfigState>(storage, "state"));
        Assert.NotNull(ex);
        Assert.Contains("<>z__", (ex.InnerException ?? ex).Message, StringComparison.Ordinal);

        var other = GrainId.Create("test", "user-1/conv-2");
        await WriteAsync(storage, "state", StateWith(discovered.ToList()), other);
        var restored = await ReadAsync<UserMcpConfigState>(storage, "state", other);
        Assert.Equal(["alpha", "beta"], Assert.Single(restored.Config.Servers).KnownToolNames);
    }

    /// <summary>Every persisted state root that can be built without arguments, as listed for the Cosmos path.</summary>
    public static TheoryData<string> StateRoots() => GrainStorageSerializerTests.PersistedTypeNames();

    [DockerTheory]
    [MemberData(nameof(StateRoots))]
    public async Task PersistedStateRoot_WritesItsTypeName_AndReadsBackThroughPostgres(string typeName)
    {
        var type = typeof(AgentGrainState).Assembly.GetType(typeName)!;
        Assert.NotNull(type);
        var cs = await postgres.CreateDatabaseAsync();
        using var host = await PostgresSilo.StartAsync(cs);
        var storage = await StorageAsync(host);

        await (Task)typeof(PostgresGrainStorageTests)
            .GetMethod(nameof(RoundTripAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, [storage, cs, typeName])!;
    }

    private static async Task RoundTripAsync<T>(IGrainStorage storage, string connectionString, string typeName)
        where T : new()
    {
        await WriteAsync(storage, "state", new T());
        Assert.Contains($"\"$type\":\"{typeName}, Agent42.Agents\"", await RawPayloadAsync(connectionString), StringComparison.Ordinal);
        Assert.IsType<T>(await ReadAsync<T>(storage, "state"));
    }

    [DockerFact]
    public async Task TheSchemaBootstrap_IsIdempotent_AcrossRestarts()
    {
        var cs = await postgres.CreateDatabaseAsync();
        using (var first = await PostgresSilo.StartAsync(cs))
        {
            await WriteAsync(await StorageAsync(first), "state", GoldenStates.AgentGrain());
        }

        // A second start finds the tables, applies nothing twice, and still reads what the first wrote.
        using var second = await PostgresSilo.StartAsync(cs);
        var restored = await ReadAsync<AgentGrainState>(await StorageAsync(second), "state");
        Assert.Equal("agent-golden", restored.AgentId);
    }

    [DockerFact]
    public async Task TwoConcurrentSchemaBootstraps_DoNotRace()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var source = new NpgsqlDataSourceBuilder(cs).Build();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostgresSchema.EnsureAsync(source)));

        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM orleansquery", connection);
        Assert.True((long)(await command.ExecuteScalarAsync())! > 0);
    }
}
