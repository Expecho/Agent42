using System.Collections.Concurrent;
using System.Reflection;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Agent42.Tests.ScriptedDreamClient;

namespace Agent42.Tests;

/// <summary>An <see cref="IGrainFactory"/> holding each user's uploaded skills and profile in memory, with the grains' real
/// supersede-by-name behaviour, so <see cref="UserSkillInstaller"/> runs without a silo.</summary>
public class FakeSkillGrainFactory : DispatchProxy
{
    public ConcurrentDictionary<string, List<StoredUserSkill>> Skills { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, UserProfile> Profiles { get; } = new(StringComparer.Ordinal);

    public IGrainFactory Factory => (IGrainFactory)(object)this;

    public static FakeSkillGrainFactory Create() => (FakeSkillGrainFactory)(object)Create<IGrainFactory, FakeSkillGrainFactory>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is not { Name: nameof(IGrainFactory.GetGrain), IsGenericMethod: true })
        {
            throw new NotSupportedException($"{targetMethod?.Name} is not faked.");
        }

        var type = targetMethod.GetGenericArguments()[0];
        var grain = (Grain)(object)(type == typeof(IUserSkillsGrain) ? Create<IUserSkillsGrain, Grain>()
            : type == typeof(IUserProfileGrain) ? Create<IUserProfileGrain, Grain>()
            : throw new NotSupportedException($"{type.Name} is not faked."));
        grain.Owner = this;
        grain.Key = (string)args![0]!;
        return grain;
    }

    public class Grain : DispatchProxy
    {
        internal FakeSkillGrainFactory Owner { get; set; } = null!;
        internal string Key { get; set; } = "";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var rows = Owner.Skills.GetOrAdd(Key, _ => []);
            switch (targetMethod?.Name)
            {
                case nameof(IUserSkillsGrain.GetAsync) when targetMethod.DeclaringType == typeof(IUserSkillsGrain):
                    lock (rows)
                    {
                        return Task.FromResult(new UserSkillConfig([.. rows], rows.Count));
                    }

                case nameof(IUserSkillsGrain.SaveAsync) when targetMethod.DeclaringType == typeof(IUserSkillsGrain):
                    var skill = (StoredUserSkill)args![0]!;
                    lock (rows)
                    {
                        var old = rows.FirstOrDefault(r => string.Equals(r.Name, skill.Name, StringComparison.OrdinalIgnoreCase));
                        rows.RemoveAll(r => ReferenceEquals(r, old));
                        rows.Add(skill);
                        return Task.FromResult(old);
                    }

                case nameof(IUserProfileGrain.GetAsync):
                    return Task.FromResult(Owner.Profiles.GetValueOrDefault(Key, UserProfile.Empty));
                case nameof(IUserProfileGrain.SaveAsync):
                    Owner.Profiles[Key] = (UserProfile)args![0]!;
                    return Task.FromResult((UserProfile)args[0]!);
                default:
                    throw new NotSupportedException($"{targetMethod?.Name} is not faked.");
            }
        }
    }
}

/// <summary>The dream's skill proposals through the service (ADR 0146): the run merge, approve, dismiss and the no-installer path.</summary>
public class FleetSkillProposalServiceTests
{
    private static readonly DateTimeOffset Night = new(2026, 11, 10, 1, 30, 0, TimeSpan.Zero);
    private const string User = "alice";
    private const string Why = "Teams repeated the same deploy steps in three tasks.";

    private readonly InMemoryFleetBlobs _blobs = new();
    private readonly FleetStore _store;
    private readonly SettableTime _time = new(Night);
    private readonly LessonCatalogs _catalogs = new();
    private readonly FakeSkillGrainFactory _grains = FakeSkillGrainFactory.Create();
    private readonly InMemorySkillPackageStore _packages = new();
    private static readonly UserTelemetryContext Telemetry = new(User, "alice", null, null, null, null);

    public FleetSkillProposalServiceTests() => _store = new FleetStore(_blobs);

    private UserSkillInstaller Installer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISkillPackageStore>(_packages);
        return new UserSkillInstaller(_grains.Factory, services.BuildServiceProvider());
    }

    private FleetLessonService Service(ScriptedDreamClient client, bool withInstaller = true)
        => new(_store, new FleetOptions(),
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(client, LessonCatalogs.ModelA)),
            _catalogs, NullLogger<FleetLessonService>.Instance, _time, skills: withInstaller ? Installer() : null);

    private async Task SeedTasks(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var finished = Night.AddHours(-3).AddMinutes(-i);
            await _store.SaveTaskAsync(new FleetTaskRecord
            {
                Id = $"t{i}",
                UserId = User,
                Request = $"request {i}",
                WorkspaceId = $"w-t{i}",
                PlannerModel = LessonCatalogs.ModelA,
                Status = FleetTaskStatus.Done,
                StartedAt = finished.AddMinutes(-5),
                FinishedAt = finished,
            });
        }
    }

    private async Task Install(string name, string body = "Run the build.\nThen run the tests.", string description = "How to deploy.")
        => await Installer().InstallAsync(User, "SKILL.md", System.Text.Encoding.UTF8.GetBytes(FleetSkillProposals.BuildMarkdown(name, description, body)), autoEnable: false);

    private static FleetSkillProposal Pending(string id, string skill = "deploys", string kind = FleetSkillProposalKind.Replace,
        string? find = "Then run the tests.", string replacement = "Then run the tests and the linter.", string? description = null)
        => new()
        {
            Id = id,
            SkillName = skill,
            Kind = kind,
            Find = find,
            Replacement = replacement,
            Description = description,
            Rationale = Why,
            Evidence = ["t0", "t1", "t2"],
            CreatedAt = Night.AddHours(-1),
        };

    private Task Seed(params FleetSkillProposal[] proposals)
        => _store.SetLessonsAsync(User, new FleetLessonsDocument { SkillProposals = proposals });

    private async Task<string?> Markdown(string name) => await Installer().ReadSkillMarkdownAsync(User, name);

    [Fact]
    public async Task Run_StoresSkillProposals()
    {
        await SeedTasks(5);
        await Install("deploys");
        var client = new ScriptedDreamClient(c => c.Number == 1
            ? ProposeSkill("deploys", "append", null, null, "Post the build link.", Why, [.. c.Tasks.Take(3).Select(t => t.Id)])
            : Say("done"));

        var result = await Service(client).RunManualAsync(User, null, Telemetry);

        Assert.Equal(1, result.Run!.SkillsProposed);
        var doc = await _store.GetLessonsAsync(User);
        var stored = Assert.Single(doc.SkillProposals);
        Assert.Equal((FleetPromptProposalStatus.Pending, "deploys"), (stored.Status, stored.SkillName));
        Assert.Contains("deploys", client.UserText);
        Assert.Contains("=== Skill deploys ===", client.UserText);
        // Nothing changed until the user decides.
        Assert.DoesNotContain("build link", await Markdown("deploys"));
    }

    [Fact]
    public async Task NoInstaller_DegradesGracefully()
    {
        await SeedTasks(5);
        await Seed(Pending("p"));
        var client = new ScriptedDreamClient(_ => Say("nothing"));
        var service = Service(client, withInstaller: false);

        await service.RunManualAsync(User, null, Telemetry);
        var approve = await service.ApproveSkillProposalAsync(User, "p");

        Assert.DoesNotContain("propose_skill_change", client.ToolNames);
        Assert.DoesNotContain("installed skills", client.UserText);
        Assert.Equal(FleetProposalOutcome.Conflict, approve.Outcome);
        Assert.Contains("not available", approve.Error);
        Assert.Equal(FleetPromptProposalStatus.Pending, (await _store.GetLessonsAsync(User)).SkillProposals.Single().Status);
    }

    [Fact]
    public async Task ApproveSkillEdit_InstallsNewText_KeepsOtherEntries_DoesNotReEnable()
    {
        await Install("deploys");
        // A skill with a resource file; the profile has a materialised selection WITHOUT the skill (the user switched it off).
        var zip = ZipTestData.Zip(("deploys/SKILL.md", FleetSkillProposals.BuildMarkdown("deploys", "How to deploy.", "Run the build.\nThen run the tests.")), ("deploys/references/notes.md", "NOTES"));
        await Installer().InstallAsync(User, "deploys.zip", zip, autoEnable: false);
        _grains.Profiles[User] = UserProfile.Empty with { EnabledTools = [ToolCatalog.WebSearch] };
        await Seed(Pending("p"));

        var decision = await Service(new ScriptedDreamClient(_ => Say("x"))).ApproveSkillProposalAsync(User, "p");

        Assert.Equal((FleetProposalOutcome.Done, "deploys"), (decision.Outcome, decision.SkillName));
        Assert.Contains("and the linter", await Markdown("deploys"));
        Assert.StartsWith("---\nname: deploys\ndescription: How to deploy.\n---\n", await Markdown("deploys"));
        var row = (await Installer().ListAsync(User)).Single();
        Assert.Contains("references/notes.md", row.Files);
        Assert.DoesNotContain(ToolCatalog.SkillKey("deploys"), _grains.Profiles[User].EnabledTools!);
        Assert.Equal(FleetPromptProposalStatus.Approved, (await _store.GetLessonsAsync(User)).SkillProposals.Single().Status);
    }

    [Fact]
    public async Task ApproveSkillEdit_SkillGone_IsOutdated()
    {
        await Seed(Pending("p"));
        var service = Service(new ScriptedDreamClient(_ => Say("x")));

        var gone = await service.ApproveSkillProposalAsync(User, "p");

        Assert.Equal(FleetProposalOutcome.Conflict, gone.Outcome);
        Assert.Contains("no longer installed", gone.Error);
        Assert.Equal(FleetPromptProposalStatus.Outdated, (await _store.GetLessonsAsync(User)).SkillProposals.Single().Status);

        // Re-uploaded with other text: the passage is gone too.
        await Seed(Pending("q"));
        await Install("deploys", body: "A completely different body.");
        var changed = await service.ApproveSkillProposalAsync(User, "q");
        Assert.Contains("changed since", changed.Error);
        Assert.Equal(FleetPromptProposalStatus.Outdated, (await _store.GetLessonsAsync(User)).SkillProposals.Single().Status);
    }

    [Fact]
    public async Task ApproveSkillEdit_AppendAlreadyThere_IsApprovedWithoutAnInstall()
    {
        await Install("deploys", body: "Run the build.\n\nPost the build link.");
        var before = (await Installer().ListAsync(User)).Single().Id;
        await Seed(Pending("p", kind: FleetSkillProposalKind.Append, find: null, replacement: "Post the build link."));

        var decision = await Service(new ScriptedDreamClient(_ => Say("x"))).ApproveSkillProposalAsync(User, "p");

        Assert.Equal(FleetProposalOutcome.Done, decision.Outcome);
        Assert.Equal(before, (await Installer().ListAsync(User)).Single().Id);
    }

    [Fact]
    public async Task ApproveSkillCreate_InstallsAndAutoEnables_DoubleApproveInstallsOnce()
    {
        _grains.Profiles[User] = UserProfile.Empty with { EnabledTools = [ToolCatalog.WebSearch] };
        await Seed(Pending("p", "release-notes", FleetSkillProposalKind.Create, null, "Group changes by area.", "How to write release notes."));
        var service = Service(new ScriptedDreamClient(_ => Say("x")));

        var both = await Task.WhenAll(service.ApproveSkillProposalAsync(User, "p"), service.ApproveSkillProposalAsync(User, "p"));

        Assert.Equal([FleetProposalOutcome.Done, FleetProposalOutcome.Conflict], both.Select(b => b.Outcome).Order());
        Assert.Single(await Installer().ListAsync(User));
        Assert.Contains("Group changes by area.", await Markdown("release-notes"));
        Assert.Contains(ToolCatalog.SkillKey("release-notes"), _grains.Profiles[User].EnabledTools!);
    }

    [Fact]
    public async Task ApproveSkillCreate_CapAndNameClash_AreConflicts()
    {
        var service = Service(new ScriptedDreamClient(_ => Say("x")));
        await Seed(Pending("clash", "deploys", FleetSkillProposalKind.Create, null, "Other body.", "How to deploy."),
            Pending("same", "twin", FleetSkillProposalKind.Create, null, "Same body.", "A twin."));
        await Install("deploys");
        await Install("twin", "Same body.", "A twin.");

        var clash = await service.ApproveSkillProposalAsync(User, "clash");
        var same = await service.ApproveSkillProposalAsync(User, "same");

        Assert.Equal(FleetProposalOutcome.Conflict, clash.Outcome);
        Assert.Equal(FleetProposalOutcome.Done, same.Outcome);
        var doc = await _store.GetLessonsAsync(User);
        Assert.Equal(FleetPromptProposalStatus.Outdated, doc.SkillProposals.Single(p => p.Id == "clash").Status);
        Assert.Equal(FleetPromptProposalStatus.Approved, doc.SkillProposals.Single(p => p.Id == "same").Status);

        // A full user: a conflict, and the proposal stays pending so the user can free a slot.
        for (var i = 0; i < SkillToolLimits.MaxInstalledSkillsPerUser - 2; i++)
        {
            await Install($"filler-{i}");
        }

        await Seed(Pending("full", "one-more", FleetSkillProposalKind.Create, null, "Body.", "One more."));
        var full = await service.ApproveSkillProposalAsync(User, "full");
        Assert.Equal(FleetProposalOutcome.Conflict, full.Outcome);
        Assert.Contains("Delete one first", full.Error);
        Assert.Equal(FleetPromptProposalStatus.Pending, (await _store.GetLessonsAsync(User)).SkillProposals.Single().Status);
    }

    [Fact]
    public async Task DismissSkillProposal_KeepsATombstone()
    {
        await Seed(Pending("p"));
        var service = Service(new ScriptedDreamClient(_ => Say("x")));

        Assert.Equal(FleetProposalOutcome.Done, (await service.DismissSkillProposalAsync(User, "p")).Outcome);

        var stored = (await _store.GetLessonsAsync(User)).SkillProposals.Single();
        Assert.Equal((FleetPromptProposalStatus.Dismissed, Night), (stored.Status, stored.DecidedAt));
        Assert.Equal(FleetProposalOutcome.Conflict, (await service.DismissSkillProposalAsync(User, "p")).Outcome);
        Assert.Equal(FleetProposalOutcome.NotFound, (await service.DismissSkillProposalAsync(User, "ghost")).Outcome);
        Assert.Equal(FleetProposalOutcome.NotFound, (await service.ApproveSkillProposalAsync("bob", "p")).Outcome);
    }

    [Fact]
    public async Task NewCount_CountsPendingSkillProposals()
    {
        await Seed(Pending("new"), Pending("old", "other") with { CreatedAt = Night.AddDays(-3) });
        var doc = await _store.GetLessonsAsync(User);
        doc = doc with { Settings = doc.Settings with { SeenAt = Night.AddDays(-1) } };

        Assert.Equal(1, FleetLessonService.NewCount(doc));
    }

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
