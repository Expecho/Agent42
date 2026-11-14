using System.Reflection;
using Npgsql;

namespace Agent42.Backend;

/// <summary>
/// Creates the local provider's Postgres schema, idempotently and safely against a second start.
///
/// <para>Orleans' ADO.NET providers do not create their tables, and the NuGet packages ship no SQL: the three scripts
/// (<c>Storage/Sql/</c>, embedded) are Orleans v10.3.1's own, and they are NOT idempotent (a second run fails with
/// <c>42P07 relation already exists</c>). So each is applied only when its table is missing, in order (Main creates
/// <c>OrleansQuery</c>, which the other two insert into), all under one session-level advisory lock so two starts cannot
/// race. Agent42's own tables are <c>IF NOT EXISTS</c> and re-run every start.</para>
/// </summary>
public static class PostgresSchema
{
    /// <summary>The dimension of <c>text-embedding-3-large</c>, matching the Cosmos container (ADR 0007).</summary>
    public const int DefaultDocChunkDimensions = 3072;

    /// <summary>The advisory-lock key for schema bootstrap. A constant: all instances must agree.</summary>
    private const long SchemaLockKey = 0x4147_3432_5343_4845; // "AG42SCHE"

    // (script, the table whose absence means the script has not run yet)
    private static readonly (string Script, string Table)[] OrleansScripts =
    [
        ("PostgreSQL-Main.sql", "orleansquery"),
        ("PostgreSQL-Persistence.sql", "orleansstorage"),
        ("PostgreSQL-Reminders.sql", "orleansreminderstable"),
    ];

    public static async Task EnsureAsync(
        NpgsqlDataSource dataSource, int docChunkDimensions = DefaultDocChunkDimensions, CancellationToken cancellationToken = default)
    {
        // A held connection: the advisory lock is per session, so it lives exactly as long as this connection.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({SchemaLockKey})", cancellationToken);
        try
        {
            foreach (var (script, table) in OrleansScripts)
            {
                if (!await TableExistsAsync(connection, table, cancellationToken))
                {
                    await ExecuteAsync(connection, ReadScript(script), cancellationToken);
                }
            }

            foreach (var script in Agent42Scripts)
            {
                await ExecuteAsync(
                    connection, ReadScript(script).Replace("{DIMENSIONS}", docChunkDimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
            }

            // Npgsql reads the database's type catalog when the first connection opens, which is this one, before the
            // `vector` extension existed on a fresh database. Without a reload every later connection fails to resolve the
            // type ("Cannot resolve 'vector'") until the app restarts.
            await connection.ReloadTypesAsync(cancellationToken);
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({SchemaLockKey})", CancellationToken.None);
        }
    }

    /// <summary>Agent42's own idempotent scripts, applied after Orleans'. Grown by the document and chat-search indexes.</summary>
    private static readonly string[] Agent42Scripts = ["agent42.sql"];

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        command.Parameters.AddWithValue("name", table);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadScript(string name)
    {
        var assembly = typeof(PostgresSchema).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Settings of the Postgres side of the local provider.</summary>
/// <param name="DocChunkDimensions">The vector size of the document index (<c>Storage:Local:DocChunksDimensions</c>).</param>
public sealed record LocalPostgresOptions(int DocChunkDimensions);

/// <summary>Runs <see cref="PostgresSchema"/> before the silo starts (hosted services start in registration order, and
/// the Orleans silo is registered after the storage provider).</summary>
public sealed class PostgresSchemaInitializer(NpgsqlDataSource dataSource, LocalPostgresOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => PostgresSchema.EnsureAsync(dataSource, options.DocChunkDimensions, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
