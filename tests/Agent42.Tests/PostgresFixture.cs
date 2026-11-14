using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Orleans.Hosting;
using Testcontainers.PostgreSql;

namespace Agent42.Tests;

/// <summary>Whether a Docker daemon answers. Checked once per run; every Postgres test skips (never fails) without one.</summary>
public static class DockerAvailability
{
    public static readonly bool IsAvailable = Probe();

    public const string SkipReason = "Docker is not available: the Postgres tests need a Docker daemon (Testcontainers).";

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips when no Docker daemon is available (xunit 2 has no runtime skip).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when no Docker daemon is available.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}

/// <summary>
/// One pgvector Postgres container for every Postgres test class (a collection fixture, so they run serially against one
/// server). Each test gets its own database from <see cref="CreateDatabaseAsync"/>, so none sees another's rows.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private int _databases;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty database on the shared server and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"t{Interlocked.Increment(ref _databases)}_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// A real Orleans silo whose storage is the LOCAL provider, built through the same <c>AddPostgres</c> and
/// <c>UseLocalStorage</c> calls <c>Program.cs</c> reaches. No grains of the app run in it: tests drive the registered
/// storage provider and reminder table directly, which is exactly the seam the Postgres provider owns.
/// </summary>
public static class PostgresSilo
{
    public static async Task<IHost> StartAsync(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:postgres"] = connectionString });
        builder.Services.AddPostgres(builder.Configuration);
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(FreePort(), FreePort());
            silo.UseLocalStorage(builder.Configuration);
        });

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
