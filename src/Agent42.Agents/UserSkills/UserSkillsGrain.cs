using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.UserSkillsState")]
public sealed class UserSkillsState
{
    // UserSkillConfig.Empty is a factory (fresh instance), so this is a per-state default, never a shared
    // mutable static — see the note on UserSkillConfig.Empty.
    [Id(0)]
    public UserSkillConfig Config { get; set; } = UserSkillConfig.Empty;
}

/// <summary>
/// Per-user uploaded-skill overlay grain (ADR 0049). Rides the shared "agent-state" storage provider on a
/// distinct "user-skills" state name (like <see cref="UserMcpConfigGrain"/>'s "mcp-servers"), so it is a
/// separate Cosmos item from the 2 MB session (ADR 0004) and needs <b>no new Cosmos container</b>.
/// </summary>
public sealed class UserSkillsGrain : Grain, IUserSkillsGrain
{
    private readonly IPersistentState<UserSkillsState> _state;

    public UserSkillsGrain(
        [PersistentState("user-skills", AgentGrain.StorageProviderName)] IPersistentState<UserSkillsState> state)
    {
        _state = state;
    }

    public Task<UserSkillConfig> GetAsync() => Task.FromResult(_state.State.Config);

    public Task<int> GetVersionAsync() => Task.FromResult(_state.State.Config.Version);

    public async Task<StoredUserSkill?> SaveAsync(StoredUserSkill skill)
    {
        var skills = _state.State.Config.Skills.ToList();

        // Upsert by NAME, not id: the id is fresh on every upload (immutable materialization directories),
        // so identifying by id would make a re-upload a second, duplicate entry. Case-insensitive because
        // a name is also a directory name, and NTFS and ext4 disagree about whether two that differ only in
        // case are one thing — the merge must not depend on which one the code happens to run on.
        var existing = skills.FindIndex(s => string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase));

        StoredUserSkill? superseded = null;
        if (existing >= 0)
        {
            superseded = skills[existing];
            skills[existing] = skill;
        }
        else
        {
            skills.Add(skill);
        }

        await BumpAsync(_state.State.Config with { Skills = skills });
        return superseded;
    }

    public async Task<StoredUserSkill?> DeleteAsync(string id)
    {
        var skills = _state.State.Config.Skills.ToList();
        var index = skills.FindIndex(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var removed = skills[index];
        skills.RemoveAt(index);
        await BumpAsync(_state.State.Config with { Skills = skills });
        return removed;
    }

    // Every write bumps the monotonic version, which the per-user skill provider keys its cache on — so an
    // upload is picked up on the next grain activation without an app restart.
    private async Task BumpAsync(UserSkillConfig next)
    {
        _state.State.Config = next with { Version = next.Version + 1 };
        await _state.WriteStateAsync();
    }
}
