using System.Reflection;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IGrainFactory"/> whose only grain is an <see cref="IUserProfileGrain"/> returning a fixed profile, for
/// code that reads the profile without a silo (the fleet's tool catalog, ADR 0099). Counts the reads. Any other call
/// throws, so a test that reaches past the profile fails loudly.
/// </summary>
public class FakeProfileGrainFactory : DispatchProxy
{
    private UserProfile _profile = UserProfile.Empty;
    private int _reads;

    /// <summary>How often a profile was read.</summary>
    public int Reads => _reads;

    public IGrainFactory Factory => (IGrainFactory)(object)this;

    public static FakeProfileGrainFactory Create(IReadOnlyList<string>? enabledTools)
    {
        var fake = (FakeProfileGrainFactory)(object)Create<IGrainFactory, FakeProfileGrainFactory>();
        fake._profile = UserProfile.Empty with { EnabledTools = enabledTools };
        return fake;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is { Name: nameof(IGrainFactory.GetGrain), IsGenericMethod: true }
            && targetMethod.GetGenericArguments()[0] == typeof(IUserProfileGrain))
        {
            var grain = (ProfileGrain)(object)Create<IUserProfileGrain, ProfileGrain>();
            grain.Owner = this;
            return grain;
        }

        throw new NotSupportedException($"{targetMethod?.Name} is not faked.");
    }

    public class ProfileGrain : DispatchProxy
    {
        internal FakeProfileGrainFactory Owner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IUserProfileGrain.GetAsync))
            {
                throw new NotSupportedException($"{targetMethod?.Name} is not faked.");
            }

            Interlocked.Increment(ref Owner._reads);
            return Task.FromResult(Owner._profile);
        }
    }
}
