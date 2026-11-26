using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The skill proposals of the dream (ADR 0146): reading the user's installed skills for the dream, and approving or dismissing a
/// proposal. Approve re-applies an edit to the CURRENT <c>SKILL.md</c> and installs through <see cref="UserSkillInstaller"/>, the
/// one install path the upload endpoint also uses. The whole approve runs under the user's lessons lock, like a prompt approve.
/// </summary>
public sealed partial class FleetLessonService
{
    private static readonly FleetProposalDecision SkillsUnavailable =
        new(FleetProposalOutcome.Conflict, "Skills are not available on this deployment.");

    private bool SkillsAvailable => skills is { Available: true };

    /// <summary>The user's installed skills with their current text, or null when this deployment cannot install skills (the dream
    /// then offers no skill tool). A skill whose package is gone is left out.</summary>
    internal async Task<IReadOnlyList<FleetSkillSnapshot>?> SkillSnapshotsAsync(string userId, CancellationToken cancellationToken)
    {
        if (!SkillsAvailable)
        {
            return null;
        }

        var result = new List<FleetSkillSnapshot>();
        foreach (var row in await skills!.ListAsync(userId))
        {
            if (await skills.ReadSkillMarkdownAsync(userId, row.Name, cancellationToken) is { } markdown)
            {
                result.Add(new FleetSkillSnapshot(row.Name, row.Description, markdown));
            }
        }

        return result;
    }

    /// <summary>Approves a pending skill proposal: an edit is re-applied to the skill's current <c>SKILL.md</c> and installed
    /// without re-enabling it; a creation is installed and turned on. A proposal that no longer applies is marked outdated.</summary>
    public Task<FleetProposalDecision> ApproveSkillProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => WithUserLockAsync(userId, async () =>
        {
            if (!SkillsAvailable)
            {
                return SkillsUnavailable;
            }

            var document = await store.GetLessonsAsync(userId, cancellationToken);
            if (SkillPendingProblem(document, proposalId) is { } problem)
            {
                return problem;
            }

            var proposal = document.SkillProposals.First(p => p.Id == proposalId);
            var (decision, status) = proposal.Kind == FleetSkillProposalKind.Create
                ? await InstallCreateAsync(userId, proposal, cancellationToken)
                : await InstallEditAsync(userId, proposal, cancellationToken);
            if (status is not null)
            {
                await store.SetLessonsAsync(userId, DecideSkill(document, proposalId, status), cancellationToken);
            }

            return decision;
        }, cancellationToken);

    /// <summary>Dismisses a pending skill proposal; it stays as a tombstone so the same change is not proposed again.</summary>
    public Task<FleetProposalDecision> DismissSkillProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => WithUserLockAsync(userId, async () =>
        {
            var document = await store.GetLessonsAsync(userId, cancellationToken);
            if (SkillPendingProblem(document, proposalId) is { } problem)
            {
                return problem;
            }

            await store.SetLessonsAsync(userId, DecideSkill(document, proposalId, FleetPromptProposalStatus.Dismissed), cancellationToken);
            return new FleetProposalDecision(FleetProposalOutcome.Done);
        }, cancellationToken);

    private static (FleetProposalDecision, string?) Outdated(string message)
        => (new FleetProposalDecision(FleetProposalOutcome.Conflict, message), FleetPromptProposalStatus.Outdated);

    // The decision and the status to record (null: leave the proposal pending).
    private async Task<(FleetProposalDecision Decision, string? Status)> InstallEditAsync(string userId, FleetSkillProposal proposal, CancellationToken cancellationToken)
    {
        const string Changed = "The skill changed since this was proposed: ";
        var row = (await skills!.ListAsync(userId)).FirstOrDefault(s => string.Equals(s.Name, proposal.SkillName, StringComparison.OrdinalIgnoreCase));
        if (row is null || FleetSkillProposals.ShippedNames().Contains(row.Name))
        {
            return Outdated(Changed + "the skill is no longer installed.");
        }

        var zip = await skills.ReadPackageAsync(userId, row.Id, cancellationToken);
        if (zip is null || SkillPackage.ReadSkillMarkdown(zip, row.Name) is not { } current)
        {
            return Outdated(Changed + "its text could not be read.");
        }

        var applied = FleetSkillProposals.Apply(current, proposal);
        if (!applied.Ok)
        {
            return Outdated(Changed + applied.Problem);
        }

        var done = new FleetProposalDecision(FleetProposalOutcome.Done, null, null, row.Name);
        if (applied.AlreadyApplied)
        {
            return (done, FleetPromptProposalStatus.Approved);
        }

        var rebuilt = SkillPackage.ReplaceSkillMarkdown(zip, row.Name, applied.Markdown!);
        var result = rebuilt is null ? null : await skills.InstallAsync(userId, row.Name + ".zip", rebuilt, autoEnable: false, cancellationToken);
        return result is { Error: null } ? (done, FleetPromptProposalStatus.Approved) : Outdated(Changed + (result?.Error ?? "its package could not be rebuilt."));
    }

    private async Task<(FleetProposalDecision Decision, string? Status)> InstallCreateAsync(string userId, FleetSkillProposal proposal, CancellationToken cancellationToken)
    {
        var markdown = FleetSkillProposals.BuildMarkdown(proposal.SkillName, proposal.Description ?? "", proposal.Replacement);
        var done = new FleetProposalDecision(FleetProposalOutcome.Done, null, null, proposal.SkillName);
        var rows = await skills!.ListAsync(userId);

        if (FleetSkillProposals.ShippedNames().Contains(proposal.SkillName))
        {
            return Outdated($"'{proposal.SkillName}' is now the name of a built-in skill.");
        }

        if (rows.Any(s => string.Equals(s.Name, proposal.SkillName, StringComparison.OrdinalIgnoreCase)))
        {
            var existing = await skills.ReadSkillMarkdownAsync(userId, proposal.SkillName, cancellationToken);
            return existing is not null && FleetPromptProposals.Normalize(existing).Trim() == markdown.Trim()
                ? (done, FleetPromptProposalStatus.Approved)
                : Outdated($"A skill named '{proposal.SkillName}' is already installed with other text.");
        }

        if (rows.Count >= SkillToolLimits.MaxInstalledSkillsPerUser)
        {
            return (new FleetProposalDecision(FleetProposalOutcome.Conflict,
                $"You already have {SkillToolLimits.MaxInstalledSkillsPerUser} skills installed. Delete one first, then approve."), null);
        }

        var result = await skills.InstallAsync(userId, "SKILL.md", System.Text.Encoding.UTF8.GetBytes(markdown), autoEnable: true, cancellationToken);
        return result.Error is null ? (done, FleetPromptProposalStatus.Approved) : Outdated(result.Error);
    }

    private static FleetProposalDecision? SkillPendingProblem(FleetLessonsDocument document, string proposalId)
        => document.SkillProposals.FirstOrDefault(p => p.Id == proposalId) switch
        {
            null => new FleetProposalDecision(FleetProposalOutcome.NotFound, "No such proposal."),
            { Status: FleetPromptProposalStatus.Pending } => null,
            var decided => new FleetProposalDecision(FleetProposalOutcome.Conflict, $"This proposal was already {decided.Status}."),
        };

    private FleetLessonsDocument DecideSkill(FleetLessonsDocument document, string proposalId, string status)
    {
        var now = _time.GetUtcNow();
        return document with
        {
            SkillProposals = [.. document.SkillProposals.Select(p => p.Id == proposalId ? p with { Status = status, DecidedAt = now } : p)],
        };
    }
}
