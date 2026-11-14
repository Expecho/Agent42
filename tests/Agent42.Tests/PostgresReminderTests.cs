using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace Agent42.Tests;

/// <summary>AC5: a reminder written to the Postgres reminder table survives a silo restart.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresReminderTests(PostgresFixture postgres)
{
    [DockerFact]
    public async Task AReminder_SurvivesASiloRestart()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var grainId = GrainId.Create("test", "user-1/conv-1");

        using (var first = await PostgresSilo.StartAsync(cs))
        {
            var table = first.Services.GetRequiredService<IReminderTable>();
            await table.UpsertRow(new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = "daily-standup",
                StartAt = DateTime.UtcNow.AddHours(1),
                Period = TimeSpan.FromHours(24),
            });
            Assert.NotNull(await table.ReadRow(grainId, "daily-standup"));
        }

        using var second = await PostgresSilo.StartAsync(cs);
        var restored = await second.Services.GetRequiredService<IReminderTable>().ReadRow(grainId, "daily-standup");

        Assert.NotNull(restored);
        Assert.Equal("daily-standup", restored.ReminderName);
        Assert.Equal(TimeSpan.FromHours(24), restored.Period);
    }

    [DockerFact]
    public async Task ADeletedReminder_StaysGone_AfterARestart()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var grainId = GrainId.Create("test", "user-2/conv-1");

        using (var first = await PostgresSilo.StartAsync(cs))
        {
            var table = first.Services.GetRequiredService<IReminderTable>();
            var etag = await table.UpsertRow(new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = "once",
                StartAt = DateTime.UtcNow.AddHours(1),
                Period = TimeSpan.FromHours(1),
            });
            Assert.True(await table.RemoveRow(grainId, "once", etag));
        }

        using var second = await PostgresSilo.StartAsync(cs);
        Assert.Null(await second.Services.GetRequiredService<IReminderTable>().ReadRow(grainId, "once"));
    }
}
