using Hindsight.AgentFramework;

namespace Agent42.Agents;

/// <summary>
/// Long-term memory across conversations (ADR 0056): a Hindsight context provider that recalls this
/// user's memories into the agent's instructions before each model call and retains the turn's
/// transcript afterwards. One bank per user, keyed on the userId parsed off the grain key.
///
/// Since ADR 0057 it is no longer always-on: it is a selectable tool (ToolCatalog.Memory), gated at the
/// three ADR 0021 layers on top of the operator's Hindsight:ApiKey.
/// </summary>
public sealed partial class AgentGrain
{
    // Builds the memory provider, or null when it is off. Two independent gates, and the ORDER matters:
    //   1. no Hindsight client configured — the OPERATOR's off switch (see HindsightIntegration), and the
    //      reason UnconfiguredMemory_MakesNoCalls asserts ZERO calls rather than failing ones. Keep it FIRST.
    //   2. the effective selection does not contain ToolCatalog.Memory — the USER's off switch (ADR 0057).
    // Attached in BuildAgent alongside the RAG provider.
    //
    // WHY THE SELECTION IS READ HERE, AT BUILD TIME, WHILE _ephemeral BELOW IS NOT. The two gates have
    // deliberately different lifetimes and folding one into the other is wrong in both directions. The two
    // layers a user changes deliberately both REBUILD the agent — SetEnabledToolsAsync does the explicit
    // snapshot/rebuild/restore/write dance, BindAgentAsync rebuilds, and activation builds from scratch —
    // so a value read here cannot go stale on them. _ephemeral cannot be read this way at all: it is
    // turn-scoped, and nothing rebuilds between turns of a warm activation. Do NOT "fix" IsEnabled into
    // carrying the selection too, and do NOT move this check into a delegate.
    // TogglingMemoryMidChat_DetachesOnTheSameActivation is the guard on the rebuild half.
    //
    // THE ONE LAYER THAT IS STALE, and it is not a defect introduced here: _userDefaultEnabledTools is
    // assigned only in OnActivateAsync, so changing the Preferences DEFAULT does not rebuild a warm grain
    // (the documented accepted staleness of ADR 0021, identical for every key). The stakes differ though —
    // a stale web_search leaves a tool available one activation too long, while a stale memory keeps
    // POSTing the conversation to a third party. The per-conversation override is the immediate control.
    // Said plainly because an earlier draft claimed the read "can never go stale", which is false.
    //
    // The gate lives in this small partial rather than as a fourth `if` in BuildAgent's contextProviders
    // block on purpose: cognitive_complexity_max is 15, enforced at build time by S3776 at error severity
    // (ADR 0050/0051), and BuildAgent already sits near it — ADR 0056 divergence #3 records this same
    // feature tipping a composition root over by adding one branch. Passing an argument adds none there.
    //
    // THE ORDERING TRAP, and the reason IsEnabled is a delegate rather than a bool. BuildAgent runs on
    // ACTIVATION and on the five rebuild paths (BindAgentAsync, SwitchModelAsync, RebindModelAsync,
    // RegisterDocumentAsync, SetEnabledToolsAsync) — never per turn. _ephemeral is turn-scoped: set at the
    // top of ChatAsync and cleared in its finally. A provider that captured `_ephemeral` as a VALUE here
    // would freeze whatever the flag happened to be when the agent was built, and then be wrong for every
    // later turn of a warm activation — silently retaining an incognito chat, or silently refusing to
    // remember a normal one. Reading it through a closure over `this` defers the question to invoke time.
    // This is the BuildDocumentSearchProvider shape (a method group closing over the grain). The
    // regression guard is EphemeralTurn_NeitherRecallsNorRetains + MemoryResumes_OnTheNormalTurnAfterAn-
    // EphemeralOne, and which of the two actually catches a captured value was MEASURED (see the table on
    // those tests): the realistic bug fails both, but a gate stuck permanently OFF fails only
    // MemoryResumes_… — so that is the load-bearing one, and deleting it as redundant is backwards.
    //
    // The bank is read from the grain key rather than from _userId so it carries no dependency on
    // OnActivateAsync having run its assignments first.
    private HindsightMemoryProvider? BuildMemoryProvider(ISet<string> enabled)
    {
        if (_memory.Client is null || !enabled.Contains(ToolCatalog.Memory))
        {
            return null;
        }

        return new HindsightMemoryProvider(
            _memory.Client,
            new HindsightOptions
            {
                BankId = UserIdFromGrainKey(this.GetPrimaryKeyString()),
                Mission = _memory.Mission,
                IsEnabled = () => !_ephemeral,
            });
    }
}
