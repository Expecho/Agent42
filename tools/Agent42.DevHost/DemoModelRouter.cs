using Agent42.Agents;

namespace Agent42.DevHost;

/// <summary>
/// The offline Auto router (ADR 0137): a deterministic stand-in for Decision-1 so the Auto picker entry, the routed label
/// and the sticky rule are exercisable with no Foundry resource. Real routing is the Backend's <c>CatalogModelRouter</c>; this
/// only decides by keyword over the same model ids the offline catalog lists.
///
/// <para>Say <c>mini</c> or <c>quick</c> to get the small model, <c>code</c> or <c>think</c> to get the reasoning one, anything
/// else keeps whatever the conversation is on.</para>
/// </summary>
public sealed class DemoModelRouter(string smallModel, string reasoningModel) : IModelRouter
{
    public Task<RouterDecision?> RouteAsync(RouterTurn turn, CancellationToken cancellationToken = default)
    {
        var text = turn.Message;
        var chosen = Contains(text, "mini", "quick") ? smallModel
            : Contains(text, "code", "think") ? reasoningModel
            : null;

        return Task.FromResult<RouterDecision?>(chosen is null ? null : new RouterDecision(chosen, chosen, chosen != turn.CurrentModelId));
    }

    private static bool Contains(string text, params string[] words)
        => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}
