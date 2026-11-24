using System.Collections.Concurrent;
using System.Reflection;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IGrainFactory"/> for the fleet's personal tools (ADR 0144) without a silo: a profile, each user's
/// conversation list (<see cref="IConversationIndexGrain"/>) and push subscriptions (<see cref="IUserPushGrain"/>), keyed by
/// the grain key, so a test can prove a tool reads only its own user's. Any other grain or call throws.
/// </summary>
public class FakeUserGrainFactory : DispatchProxy
{
    public UserProfile Profile { get; set; } = UserProfile.Empty;
    public ConcurrentDictionary<string, IReadOnlyList<ConversationInfo>> Conversations { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, IReadOnlyList<PushSubscription>> Devices { get; } = new(StringComparer.Ordinal);

    public IGrainFactory Factory => (IGrainFactory)(object)this;

    public static FakeUserGrainFactory Create(IReadOnlyList<string>? enabledTools = null)
    {
        var fake = (FakeUserGrainFactory)(object)Create<IGrainFactory, FakeUserGrainFactory>();
        fake.Profile = UserProfile.Empty with { EnabledTools = enabledTools };
        return fake;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is not { Name: nameof(IGrainFactory.GetGrain), IsGenericMethod: true })
        {
            throw new NotSupportedException($"{targetMethod?.Name} is not faked.");
        }

        var key = (string)args![0]!;
        var type = targetMethod.GetGenericArguments()[0];
        var grain = (Grain)(object)(type == typeof(IUserProfileGrain) ? Create<IUserProfileGrain, Grain>()
            : type == typeof(IConversationIndexGrain) ? Create<IConversationIndexGrain, Grain>()
            : type == typeof(IUserPushGrain) ? Create<IUserPushGrain, Grain>()
            : throw new NotSupportedException($"{type.Name} is not faked."));
        grain.Owner = this;
        grain.Key = key;
        return grain;
    }

    public class Grain : DispatchProxy
    {
        internal FakeUserGrainFactory Owner { get; set; } = null!;
        internal string Key { get; set; } = "";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IUserProfileGrain.GetAsync) => Task.FromResult(Owner.Profile),
            nameof(IConversationIndexGrain.GetConversationsAsync)
                => Task.FromResult(Owner.Conversations.GetValueOrDefault(Key) ?? []),
            nameof(IUserPushGrain.GetAllAsync) => Task.FromResult(Owner.Devices.GetValueOrDefault(Key) ?? []),
            _ => throw new NotSupportedException($"{targetMethod?.Name} is not faked."),
        };
    }
}

/// <summary>A user's uploaded skills, fixed per test.</summary>
public sealed class FakeUserSkillProvider(params SkillInfo[] skills) : IUserSkillProvider
{
    public Task<UserSkillSet> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(new UserSkillSet(skills, 1));

    public void RemoveMaterialized(string skillId)
    {
    }
}

/// <summary>Skill folders on disk for a test, deleted on dispose.</summary>
public sealed class TempSkills : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "agent42-skills-" + Guid.NewGuid().ToString("N"));

    /// <summary>Writes <c>{name}/SKILL.md</c> with <paramref name="body"/>, plus the given files.</summary>
    public SkillInfo Add(string name, string body, params (string Path, string Text)[] files)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return new SkillInfo(name, $"The {name} skill.", dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
