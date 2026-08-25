using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Wraps an <see cref="AgentSkillsProvider"/> so every tool it hands the model returns a <b>bounded</b>
/// string (ADR 0049 / ADR 0004). Instructions and injected messages pass through untouched.
///
/// <para><b>Why this exists.</b> A tool's <c>FunctionResultContent</c> is persisted into the session
/// <c>AgentGrain</c> re-serializes into one 2 MB-capped Cosmos item every turn. The skills tools return
/// whole files: measured against Microsoft.Agents.AI 1.13.0, a 300 KB <c>SKILL.md</c> makes
/// <c>load_skill</c> return <b>300,087</b> characters and a 200 KB resource makes
/// <c>read_skill_resource</c> return all <b>200,000</b>. Unbounded, that result would sit in history
/// forever, be re-sent to the model every turn, and eventually make <c>WriteStateAsync</c> fail
/// <i>after</i> the model had already answered — bricking the conversation, in production only. It did not
/// bite before ADR 0049 because the shipped SKILL.md files are small, carry no resources, and
/// <c>read_skill_resource</c> was unreachable behind an approval this grain has no loop for.</para>
///
/// <para><b>Why a provider wrapper and not a subclass.</b> <see cref="AgentSkillsProvider"/> is
/// <c>sealed</c>, and it does not expose its tools as a list — it produces them inside the
/// <see cref="AIContext"/> it returns per invocation. So the only seam is the context, reached by
/// overriding <see cref="ProvideAIContextAsync"/> (the protected virtual the skills provider itself
/// overrides) and delegating to the inner provider's public entry point.</para>
///
/// <para>The tools are wrapped with <see cref="DelegatingAIFunction"/>, which preserves each tool's
/// <b>name and JSON schema</b> — exactly as <c>TruncatingMcpTool</c> does for MCP results — so the model
/// still calls them correctly and only the returned text is cut.</para>
/// </summary>
/// <para>Public rather than internal because this repo has no <c>InternalsVisibleTo</c> — the same reason
/// <see cref="SkillCatalog.Discover"/> is: the truncation guarantee is only assertable if a test can build
/// the provider and invoke a tool through it.</para>
public sealed class TruncatingSkillsProvider(AIContextProvider inner) : AIContextProvider
{
    /// <summary>
    /// Builds the skills provider exactly as <c>AgentGrain.BuildAgent</c> does — the wrapper, the script
    /// ban and the two approval flags. <b>The single place those options live</b>, so a test asserting on
    /// truncation or on script visibility cannot drift from what a conversation actually gets.
    /// </summary>
    public static AIContextProvider ForSkillPaths(IEnumerable<string> skillPaths)
        => new TruncatingSkillsProvider(new AgentSkillsProvider(
            skillPaths,
            // AllowedScriptExtensions = [] makes the FRAMEWORK refuse to discover any script at all, so
            // <available_scripts> is always empty and no script name ever reaches the model. That is a
            // second, independent control behind SkillPackage's script filter: an uploaded script that
            // somehow reached disk still cannot be advertised. run_skill_script is advertised regardless
            // (measured on 1.13.0: unconditionally, even for a skill with no scripts) but it stays
            // approval-wrapped — DisableRunSkillScriptApproval is deliberately NOT set — and no
            // AgentFileSkillScriptRunner is ever registered, so invoking it throws rather than executing.
            fileOptions: new AgentFileSkillsSourceOptions { AllowedScriptExtensions = [] },
            options: new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                // Uploaded skills carry bundled resources (ADR 0049), and this grain has no approval loop —
                // leaving this false would store resources the model could never read. It applies to shipped
                // skills too; they carry no resources, so nothing changes for them.
                DisableReadSkillResourceApproval = true,
            }));

    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    public override object? GetService(Type serviceType, object? serviceKey = null)
        => inner.GetService(serviceType, serviceKey) ?? base.GetService(serviceType, serviceKey);

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        // The inner provider is handed a FRESH, EMPTY AIContext rather than the caller's. This is
        // load-bearing, not tidiness: the public InvokingAsync ACCUMULATES onto the context it is given
        // (measured on 1.13.0 — it concatenates Instructions and unions Messages and Tools by reference).
        // Passing `context` straight through therefore hands back the request's OWN messages as though this
        // provider had injected them, and the agent then tags them AIContextProvider-sourced — which showed
        // up as document-retrieval context appearing in conversations that have no documents. With an empty
        // context, what comes back is purely the skills provider's contribution, and the framework merges
        // this method's return value exactly as it would the unwrapped provider's.
#pragma warning disable MAAI001 // InvokingContext's ctor is [Experimental]; the accumulate-on-input
        var isolated = new InvokingContext(context.Agent, context.Session, new AIContext());
#pragma warning restore MAAI001 // behaviour above leaves no non-experimental way to isolate the call.

        var produced = await inner.InvokingAsync(isolated, cancellationToken);

        // A NEW AIContext rather than mutating `produced` in place: the inner provider caches skill data and
        // there is no contract saying the context it returns is ours to modify.
        return new AIContext
        {
            Instructions = produced.Instructions,
            Messages = produced.Messages,
            Tools = produced.Tools is null ? null : [.. produced.Tools.Select(Wrap)],
        };
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
        => inner.InvokedAsync(context, cancellationToken);

    // load_skill returns a whole SKILL.md (plus the framework's resource listing); read_skill_resource
    // returns a whole file. They get different budgets because load_skill is called once per skill while a
    // model may read several resources in one turn, each persisted. Any other tool the provider might grow
    // is bounded by the smaller of the two rather than left unbounded — an unknown tool is exactly the case
    // where a default of "no limit" is wrong.
    private static AITool Wrap(AITool tool) => tool is AIFunction fn
        ? new TruncatingSkillTool(fn, fn.Name == "load_skill"
            ? SkillToolLimits.MaxSkillBodyLength
            : SkillToolLimits.MaxResourceTextLength)
        : tool;

    private sealed class TruncatingSkillTool(AIFunction fn, int limit) : DelegatingAIFunction(fn)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var raw = await base.InvokeCoreAsync(arguments, cancellationToken);

            // The skills tools return plain strings; ToString() covers anything else. Under budget the raw
            // value passes through so no structure is lost, exactly as TruncatingMcpTool does.
            var text = raw as string ?? raw?.ToString() ?? "";
            return text.Length <= limit ? raw : text[..limit] + SkillToolLimits.TruncationMarker;
        }
    }
}
