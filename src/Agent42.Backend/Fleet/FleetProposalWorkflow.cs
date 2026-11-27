using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Approving and dismissing the dream's proposals (ADR 0143 prompt edits, ADR 0146 skill edits and new skills). An approve
/// re-applies the edit to the CURRENT prompt or <c>SKILL.md</c> and runs its whole body under the user's lessons lock
/// (<see cref="FleetLessonStore.WithUserLockAsync{T}"/>), across reading and writing both <c>lessons.json</c> and
/// <c>prompts.json</c>, so a double click gets a conflict and the change is made once.
/// </summary>
internal sealed partial class FleetProposalWorkflow(
    IFleetStore store,
    FleetLessonStore lessonStore,
    UserSkillInstaller? skills,
    ILogger logger,
    TimeProvider time)
{
    /// <summary>The user's prompts as they are now: the defaults with the user's overrides.</summary>
    public async Task<FleetPromptSet> PromptsAsync(string userId, CancellationToken cancellationToken = default)
        => new(await store.GetPromptsAsync(userId, cancellationToken));

    /// <summary>
    /// Approves a pending prompt proposal: re-applies its edit to the user's CURRENT prompt and saves that as the user's own
    /// copy (<see cref="FleetPrompts.WithOverride"/>), then marks it approved. A proposal whose passage is gone is marked
    /// outdated and is a conflict. The whole body runs under the user's lock, so a double click gets a conflict and the prompt
    /// changes once. <c>prompts.json</c> itself has no lock: a prompt-editor save at the same moment can still race (ADR 0143).
    /// </summary>
    public Task<FleetProposalDecision> ApproveProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => lessonStore.WithUserLockAsync(userId, async () =>
        {
            var document = await store.GetLessonsAsync(userId, cancellationToken);
            if (PendingProblem(document, proposalId) is { } problem)
            {
                return problem;
            }

            var proposal = document.PromptProposals.First(p => p.Id == proposalId);
            var overrides = await store.GetPromptsAsync(userId, cancellationToken);
            var applied = FleetPromptProposals.Apply(new FleetPromptSet(overrides).Get(proposal.PromptKey), proposal);
            if (!applied.Ok)
            {
                await store.SetLessonsAsync(userId, Decide(document, proposalId, FleetPromptProposalStatus.Outdated), cancellationToken);
                return new FleetProposalDecision(FleetProposalOutcome.Conflict, $"The prompt changed since this was proposed: {applied.Problem}");
            }

            if (!applied.AlreadyApplied)
            {
                await store.SetPromptsAsync(userId, FleetPrompts.WithOverride(overrides, proposal.PromptKey, applied.Text), cancellationToken);
            }

            await store.SetLessonsAsync(userId, Decide(document, proposalId, FleetPromptProposalStatus.Approved), cancellationToken);
            return new FleetProposalDecision(FleetProposalOutcome.Done, null, proposal.PromptKey);
        }, cancellationToken);

    /// <summary>Dismisses a pending prompt proposal; it stays as a tombstone so the same change is not proposed again.</summary>
    public Task<FleetProposalDecision> DismissProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => lessonStore.WithUserLockAsync(userId, async () =>
        {
            var document = await store.GetLessonsAsync(userId, cancellationToken);
            if (PendingProblem(document, proposalId) is { } problem)
            {
                return problem;
            }

            await store.SetLessonsAsync(userId, Decide(document, proposalId, FleetPromptProposalStatus.Dismissed), cancellationToken);
            return new FleetProposalDecision(FleetProposalOutcome.Done);
        }, cancellationToken);

    private static FleetProposalDecision? PendingProblem(FleetLessonsDocument document, string proposalId)
        => document.PromptProposals.FirstOrDefault(p => p.Id == proposalId) switch
        {
            null => new FleetProposalDecision(FleetProposalOutcome.NotFound, "No such proposal."),
            { Status: FleetPromptProposalStatus.Pending } => null,
            var decided => new FleetProposalDecision(FleetProposalOutcome.Conflict, $"This proposal was already {decided.Status}."),
        };

    private FleetLessonsDocument Decide(FleetLessonsDocument document, string proposalId, string status)
    {
        var now = time.GetUtcNow();
        return document with
        {
            PromptProposals = [.. document.PromptProposals.Select(p => p.Id == proposalId ? p with { Status = status, DecidedAt = now } : p)],
        };
    }
}
