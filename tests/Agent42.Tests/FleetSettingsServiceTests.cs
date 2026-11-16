using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>The user's fleet overrides (ADR 0135): bounds, clamp, merge, clear, and what can never be overridden.</summary>
public class FleetSettingsServiceTests
{
    private const string User = "alice";

    private readonly FleetOptions _operator = new() { MaxAttempts = 3, MaxTeamDepth = 6, DefaultMaxTokensPerTask = 123, MaxTokensPerTaskLimit = 456 };
    private readonly FleetStore _store = new(new InMemoryFleetBlobs());
    private readonly FleetSettingsService _service;

    public FleetSettingsServiceTests() => _service = new FleetSettingsService(_operator, _store, new LessonCatalogs());

    [Fact]
    public async Task Resolve_UnsetFallsBack_SetOverrides()
    {
        Assert.Equal(3, (await _service.ResolveAsync(User)).MaxAttempts);

        Assert.Null((await _service.SaveAsync(User, new FleetUserSettings { MaxAttempts = 5, DreamAtUtc = "03:30" })).Error);

        var resolved = await _service.ResolveAsync(User);
        Assert.Equal(5, resolved.MaxAttempts);
        Assert.Equal("03:30", resolved.DreamAtUtc);
        Assert.Equal(_operator.PassingScore, resolved.PassingScore);
        Assert.Equal(3, _operator.MaxAttempts);
        Assert.NotSame(_operator, resolved);
        Assert.Equal(3, (await _service.ResolveAsync("bob")).MaxAttempts);
    }

    [Fact]
    public async Task Save_OutOfRange_Refused()
    {
        var (field, error) = await _service.SaveAsync(User, new FleetUserSettings { MaxAttempts = 99 });

        Assert.Equal(nameof(FleetOptions.MaxAttempts), field);
        Assert.NotNull(error);
        Assert.Equal(0, (await _store.GetSettingsAsync(User)).MaxAttempts ?? 0);
        Assert.Equal(nameof(FleetOptions.BudgetSoftLimit), (await _service.SaveAsync(User, new FleetUserSettings { BudgetSoftLimit = 0 })).Field);
    }

    [Fact]
    public async Task Save_DepthFields_FollowTheEffectiveTeamDepth()
    {
        // The operator's MaxTeamDepth is 6, so 7 is out of range; raising the user's depth first makes it valid.
        Assert.Equal(nameof(FleetOptions.JudgeDepth), (await _service.SaveAsync(User, new FleetUserSettings { JudgeDepth = 7 })).Field);
        Assert.Null((await _service.SaveAsync(User, new FleetUserSettings { JudgeDepth = 7, MaxTeamDepth = 8 })).Error);
        Assert.Null((await _service.SaveAsync(User, new FleetUserSettings { JudgeDepth = -1 })).Error);
    }

    [Fact]
    public async Task Save_BadTime_Refused()
    {
        Assert.Equal(nameof(FleetOptions.DreamAtUtc), (await _service.SaveAsync(User, new FleetUserSettings { DreamAtUtc = "25:00" })).Field);
        Assert.Equal(nameof(FleetOptions.DreamAtUtc), (await _service.SaveAsync(User, new FleetUserSettings { DreamAtUtc = "1am" })).Field);
    }

    [Fact]
    public async Task Save_UnknownCopilotModel_Refused_AndCopilotOnlyAccepted()
    {
        Assert.Equal(nameof(FleetOptions.DefaultCopilotModel), (await _service.SaveAsync(User, new FleetUserSettings { DefaultCopilotModel = "nope" })).Field);
        Assert.Equal(nameof(FleetOptions.DefaultCopilotModel), (await _service.SaveAsync(User, new FleetUserSettings { DefaultCopilotModel = LessonCatalogs.NoToolsModel })).Field);

        Assert.Null((await _service.SaveAsync(User, new FleetUserSettings { DefaultCopilotModel = LessonCatalogs.CopilotModel })).Error);
        Assert.Equal(LessonCatalogs.CopilotModel, (await _service.ResolveAsync(User)).DefaultCopilotModel);

        Assert.Null((await _service.SaveAsync(User, new FleetUserSettings { DefaultCopilotModel = LessonCatalogs.ModelA })).Error);
        Assert.Null((await _service.SaveAsync(User, new FleetUserSettings { DefaultCopilotModel = " " })).Error);
        Assert.Equal(_operator.DefaultCopilotModel, (await _service.ResolveAsync(User)).DefaultCopilotModel);
    }

    [Fact]
    public async Task Save_IsTheFullSet_ANullClears_AndResetClearsAll()
    {
        await _service.SaveAsync(User, new FleetUserSettings { MaxAttempts = 5, PassingScore = 9 });
        await _service.SaveAsync(User, new FleetUserSettings { PassingScore = 9 });

        var resolved = await _service.ResolveAsync(User);
        Assert.Equal(3, resolved.MaxAttempts);
        Assert.Equal(9, resolved.PassingScore);

        await _service.ResetAsync(User);
        Assert.Equal(_operator.PassingScore, (await _service.ResolveAsync(User)).PassingScore);
    }

    [Fact]
    public async Task Resolve_ClampsStoredValues()
    {
        await _store.SetSettingsAsync(User, new FleetUserSettings
        {
            MaxAttempts = 500,
            MaxFixRounds = -4,
            BudgetSoftLimit = 7.5,
            MaxTeamDepth = 3,
            JudgeDepth = 9,
            DreamAtUtc = "garbage",
            LessonMaxChars = 1,
        });

        var resolved = await _service.ResolveAsync(User);

        Assert.Equal(6, resolved.MaxAttempts);
        Assert.Equal(0, resolved.MaxFixRounds);
        Assert.Equal(1.0, resolved.BudgetSoftLimit);
        Assert.Equal(3, resolved.JudgeDepth);
        Assert.Equal(_operator.DreamAtUtc, resolved.DreamAtUtc);
        Assert.Equal(20, resolved.LessonMaxChars);
    }

    [Fact]
    public async Task OperatorOnlyFields_NotExposed()
    {
        string[] operatorOnly =
        [
            nameof(FleetOptions.MaxTokensPerTaskLimit), nameof(FleetOptions.DefaultMaxTokensPerTask), nameof(FleetOptions.MaxConcurrentTasks),
            nameof(FleetOptions.CopilotAgentsEnabled), nameof(FleetOptions.DreamEnabled), nameof(FleetOptions.DreamTimeout), nameof(FleetOptions.SseKeepAlive),
            nameof(FleetOptions.MaxEventLogBytes), nameof(FleetOptions.MaxWorkspaceBytes), nameof(FleetOptions.MaxWorkspaceFiles),
            nameof(FleetOptions.MaxInputFiles), nameof(FleetOptions.MaxInputFileBytes), nameof(FleetOptions.MaxRetryResultLength), nameof(FleetOptions.MaxToolTextLength),
        ];

        Assert.Empty(FleetSettingsService.Descriptors.Select(d => d.Key).Intersect(operatorOnly));
        Assert.Empty(typeof(FleetUserSettings).GetProperties().Select(p => p.Name).Intersect(operatorOnly));
        Assert.Equal(25, FleetSettingsService.Descriptors.Count);

        var resolved = await _service.ResolveAsync(User);
        Assert.Equal(456, resolved.MaxTokensPerTaskLimit);
        Assert.Equal(123, resolved.DefaultMaxTokensPerTask);
    }
}
