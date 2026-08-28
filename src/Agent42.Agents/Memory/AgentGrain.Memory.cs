using Hindsight.AgentFramework;

namespace Agent42.Agents;

/// <summary>
/// Long-term memory across conversations (ADR 0056): a Hindsight context provider that recalls this
/// user's memories into the agent's instructions before each model call and retains the turn's
/// transcript afterwards. One bank per user, keyed on the userId parsed off the grain key.
/// </summary>
public sealed partial class AgentGrain
{
    // Builds the always-on memory provider, or null when no Hindsight client is configured (the operator's
    // off switch — see HindsightIntegration). Attached in BuildAgent alongside the RAG provider.
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
    private HindsightMemoryProvider? BuildMemoryProvider()
    {
        if (_memory.Client is null)
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
