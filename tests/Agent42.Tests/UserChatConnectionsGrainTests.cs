using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The per-user chat bot store (ADR 0111). Two properties carry the weight: pairing binds exactly one owner, once,
/// inside its window, with a lockout; and the global run list holds a connection exactly while it is runnable, with
/// every stop path deregistering BEFORE it writes (a crash must fail toward "not run").
/// </summary>
public sealed class UserChatConnectionsGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChatBotIdentity Bot = new("7000000001", "agent42_test_bot");

    private static ChatConnectionSettings Settings(string name = "My bot", bool enabled = true)
        => new() { Name = name, Enabled = enabled };

    private (IUserChatConnectionsGrain Grain, string UserId) NewUser()
    {
        var userId = "chat-user-" + Guid.NewGuid().ToString("N");
        return (Cluster.GrainFactory.GetGrain<IUserChatConnectionsGrain>(userId), userId);
    }

    private Task<IReadOnlyList<ChatConnectionRef>> RunList()
        => Cluster.GrainFactory.GetGrain<IChatConnectionDirectoryGrain>(IChatConnectionDirectoryGrain.Key).ListAsync();

    private static async Task<ChatConnection> CreateWithCredentials(IUserChatConnectionsGrain grain)
    {
        var created = await grain.CreateAsync(ChatPlatformKind.Telegram, Settings(), Now);
        return (await grain.SetCredentialsAsync(created!.Id, "cipher", Bot, Now))!;
    }

    [Fact]
    public async Task Create_RejectsUnknownPlatform_AndCapsPerUser()
    {
        var (grain, _) = NewUser();

        Assert.Null(await grain.CreateAsync("Myspace", Settings(), Now));
        for (var i = 0; i < ChatGatewayLimits.MaxConnectionsPerUser; i++)
        {
            Assert.NotNull(await grain.CreateAsync(ChatPlatformKind.Telegram, Settings($"bot {i}"), Now));
        }

        Assert.Null(await grain.CreateAsync(ChatPlatformKind.Telegram, Settings(), Now));
    }

    [Fact]
    public async Task Create_WithoutCredentials_IsNotRun_ThenCredentialsRegisterIt()
    {
        var (grain, userId) = NewUser();
        var created = await grain.CreateAsync(ChatPlatformKind.Telegram, Settings(), Now);

        Assert.False(created!.IsRunnable);
        Assert.DoesNotContain(new ChatConnectionRef(userId, created.Id), await RunList());

        var withCredentials = await grain.SetCredentialsAsync(created.Id, "cipher", Bot, Now);

        Assert.True(withCredentials!.IsRunnable);
        Assert.Equal("7000000001", withCredentials.BotUserId);
        Assert.Contains(new ChatConnectionRef(userId, created.Id), await RunList());
    }

    [Fact]
    public async Task Disable_Fault_And_Delete_AllLeaveTheRunList()
    {
        var (grain, userId) = NewUser();
        var a = await CreateWithCredentials(grain);
        var b = await CreateWithCredentials(grain);
        var c = await CreateWithCredentials(grain);

        await grain.UpdateSettingsAsync(a.Id, Settings(enabled: false), Now);
        await grain.RecordFaultAsync(b.Id, "Telegram rejected the bot token.", Now);
        Assert.True(await grain.DeleteAsync(c.Id));

        var runList = await RunList();
        Assert.DoesNotContain(new ChatConnectionRef(userId, a.Id), runList);
        Assert.DoesNotContain(new ChatConnectionRef(userId, b.Id), runList);
        Assert.DoesNotContain(new ChatConnectionRef(userId, c.Id), runList);
        Assert.Equal("Telegram rejected the bot token.", (await grain.GetAsync(b.Id))!.FaultReason);
        Assert.Null(await grain.GetAsync(c.Id));
        Assert.False(await grain.DeleteAsync(c.Id));
    }

    [Fact]
    public async Task NewCredentials_ClearAFault_AndRunAgain()
    {
        var (grain, userId) = NewUser();
        var connection = await CreateWithCredentials(grain);
        await grain.RecordFaultAsync(connection.Id, "bad token", Now);

        var fixedUp = await grain.SetCredentialsAsync(connection.Id, "cipher-2", Bot, Now);

        Assert.Null(fixedUp!.FaultReason);
        Assert.Contains(new ChatConnectionRef(userId, connection.Id), await RunList());
    }

    [Fact]
    public async Task SavingSettings_IsATryAgain_ThatClearsAFault()
    {
        var (grain, userId) = NewUser();
        var connection = await CreateWithCredentials(grain);
        await grain.RecordFaultAsync(connection.Id, "Another program is reading this bot's messages.", Now);

        var saved = await grain.UpdateSettingsAsync(connection.Id, Settings(), Now);

        Assert.Null(saved!.FaultReason);
        Assert.Contains(new ChatConnectionRef(userId, connection.Id), await RunList());
    }

    [Fact]
    public async Task Pairing_TheRightCodeBindsTheOwnerOnce()
    {
        var (grain, _) = NewUser();
        var connection = await CreateWithCredentials(grain);
        var code = connection.PairingCode!;

        Assert.Equal(ChatPairingOutcome.Paired, await grain.TryPairAsync(connection.Id, "111111111", null, $" {code.ToLowerInvariant()} ", Now));

        var paired = await grain.GetAsync(connection.Id);
        Assert.Equal("111111111", paired!.OwnerPlatformUserId);
        Assert.Null(paired.PairingCode);
        // Single use: the same code from someone else is ignored now.
        Assert.Equal(ChatPairingOutcome.Ignored, await grain.TryPairAsync(connection.Id, "222222222", null, code, Now));
        Assert.Equal("111111111", (await grain.GetAsync(connection.Id))!.OwnerPlatformUserId);
    }

    [Fact]
    public async Task Pairing_ExpiredCode_IsIgnored()
    {
        var (grain, _) = NewUser();
        var connection = await CreateWithCredentials(grain);

        var outcome = await grain.TryPairAsync(
            connection.Id, "111111111", null, connection.PairingCode!, Now + ChatGatewayLimits.PairingCodeLifetime);

        Assert.Equal(ChatPairingOutcome.Ignored, outcome);
        Assert.False((await grain.GetAsync(connection.Id))!.IsPaired);
    }

    [Fact]
    public async Task Pairing_LocksOutAfterTooManyWrongCodes_UntilRepair()
    {
        var (grain, _) = NewUser();
        var connection = await CreateWithCredentials(grain);
        for (var i = 0; i < ChatGatewayLimits.MaxPairingFailures; i++)
        {
            Assert.Equal(ChatPairingOutcome.Ignored, await grain.TryPairAsync(connection.Id, "222222222", null, "WRONGCODE" + i, Now));
        }

        // The right code is refused once locked out.
        Assert.Equal(ChatPairingOutcome.Ignored, await grain.TryPairAsync(connection.Id, "111111111", null, connection.PairingCode!, Now));

        var repaired = await grain.RepairAsync(connection.Id, Now);
        Assert.Equal(0, repaired!.PairingFailures);
        Assert.Equal(ChatPairingOutcome.Paired, await grain.TryPairAsync(connection.Id, "111111111", null, repaired.PairingCode!, Now));
    }

    [Fact]
    public async Task Repair_ClearsTheOwner()
    {
        var (grain, _) = NewUser();
        var connection = await CreateWithCredentials(grain);
        await grain.TryPairAsync(connection.Id, "111111111", "T1", connection.PairingCode!, Now);

        var repaired = await grain.RepairAsync(connection.Id, Now);

        Assert.False(repaired!.IsPaired);
        Assert.Null(repaired.OwnerWorkspaceId);
        Assert.NotNull(repaired.PairingCode);
    }

    [Fact]
    public async Task PollOffset_NeverMovesBackwards_AndResetsWithNewCredentials()
    {
        var (grain, _) = NewUser();
        var connection = await CreateWithCredentials(grain);

        await grain.SetPollOffsetAsync(connection.Id, 500000006);
        await grain.SetPollOffsetAsync(connection.Id, 500000002);
        Assert.Equal(500000006, (await grain.GetAsync(connection.Id))!.PollOffset);

        // A different bot's cursor means nothing: new credentials restart it.
        Assert.Equal(0, (await grain.SetCredentialsAsync(connection.Id, "cipher-2", Bot, Now))!.PollOffset);
    }

    [Fact]
    public async Task Settings_TrimAndCapTheName_AndBlankTargetMeansPlainChat()
    {
        var (grain, _) = NewUser();
        var created = await grain.CreateAsync(
            ChatPlatformKind.Telegram,
            new ChatConnectionSettings { Name = "  " + new string('x', 100) + "  ", TargetAgentId = " " },
            Now);

        Assert.Equal(ChatGatewayLimits.MaxConnectionNameChars, created!.Name.Length);
        Assert.Null(created.TargetAgentId);

        var unnamed = await grain.CreateAsync(ChatPlatformKind.Slack, new ChatConnectionSettings { Name = " " }, Now);
        Assert.Equal(ChatPlatformKind.Slack, unnamed!.Name);
    }

    [Fact]
    public async Task HomeRoute_IsStored()
    {
        var (grain, _) = NewUser();
        var connection = await CreateWithCredentials(grain);

        await grain.SetHomeRouteAsync(connection.Id, "111111111");

        Assert.Equal("111111111", (await grain.GetAsync(connection.Id))!.HomeRouteKey);
    }
}
