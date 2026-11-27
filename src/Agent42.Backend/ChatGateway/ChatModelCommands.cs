using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The chat commands that read or change what a conversation runs on: <c>/model</c> (with the Auto router of ADR 0137),
/// <c>/agent</c>, <c>/home</c> and <c>/status</c>, plus the Auto-default lookup the turn driver uses. Each handler
/// returns the reply text and sends nothing itself; the pipeline owns dispatch, the queue and the reply.
///
/// <para>Absent features answer as they always did: no <see cref="IUserModelCatalogProvider"/>
/// (<paramref name="modelCatalogs"/> null) reads as "couldn't load your models"; no <see cref="IUserModelRouterProvider"/>
/// (<paramref name="routers"/> null) refuses <c>/model auto</c> and offers no Auto default.</para>
///
/// <para>Public only because the pipeline's public constructor takes it and this repo has no
/// <c>InternalsVisibleTo</c>.</para>
/// </summary>
public sealed class ChatModelCommands(
    IGrainFactory grains,
    ILogger<ChatModelCommands> logger,
    IUserModelCatalogProvider? modelCatalogs = null,
    IUserModelRouterProvider? routers = null)
{
    private const int MaxListedItems = 30;
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(10);

    internal async Task<string> ModelAsync(ChatTurnWork work, string arguments, char prefix)
    {
        var route = work.Route(grains);
        if (arguments.Length == 0)
        {
            return $"Model: {(await route.GetAsync()).ModelOverride ?? "your default model"}.";
        }

        if (arguments.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            await route.SetModelAsync(null);
            return "Back to your default model.";
        }

        // Auto (ADR 0137) is accepted only while the user has a router; a stored `auto` for a user who lost it is ignored by the grain.
        if (arguments.Equals(AutoModelId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return await SetAutoModelAsync(work, route);
        }

        if (await ModelsAsync(work.UserId) is not { } models)
        {
            return "Couldn't load your models right now. Try again in a moment.";
        }

        if (arguments.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return await ListModelsAsync(work.UserId, models);
        }

        var match = models.FirstOrDefault(m => string.Equals(m.Id, arguments, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return $"There's no model called \"{arguments}\". {prefix}model list shows yours.";
        }

        await route.SetModelAsync(match.Id);
        return $"Model set to {match.Id}. Agent conversations keep their agent's model.";
    }

    internal async Task<string> AgentAsync(ChatTurnWork work, string arguments, char prefix)
    {
        var route = work.Route(grains);
        var registry = grains.GetGrain<IAgentRegistryGrain>(work.UserId);
        if (arguments.Length == 0)
        {
            var target = (await route.GetAsync()).TargetAgentOverride ?? work.Connection.TargetAgentId;
            var agent = string.IsNullOrEmpty(target) ? null : await registry.GetAsync(target);
            return agent is null ? "New conversations here are plain chat." : $"New conversations here talk to {agent.Name}.";
        }

        if (arguments.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            await route.SetAgentAsync("");
            return "Started a new plain chat.";
        }

        var agents = await registry.GetAllAsync();
        if (arguments.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return agents.Count == 0
                ? "You have no stored agents."
                : "Agents:\n" + string.Join('\n', agents.Take(MaxListedItems).Select(a => $"• {a.Name}"));
        }

        var chosen = agents.FirstOrDefault(a => string.Equals(a.Name, arguments, StringComparison.OrdinalIgnoreCase))
            ?? agents.FirstOrDefault(a => a.Id == arguments);
        if (chosen is null)
        {
            return $"There's no agent called \"{arguments}\". {prefix}agent list shows yours.";
        }

        await route.SetAgentAsync(chosen.Id);
        return $"Started a new conversation with {chosen.Name}.";
    }

    internal async Task<string> SetHomeAsync(ChatTurnWork work)
    {
        await grains.GetGrain<IUserChatConnectionsGrain>(work.UserId).SetHomeRouteAsync(work.Connection.Id, work.RouteKey);
        return work.Connection.NotificationsEnabled
            ? "Notifications and scheduled results will come to this chat."
            : "This chat is now home. Turn on notifications for this connection in Agent42 to receive them here.";
    }

    internal async Task<string> StatusAsync(ChatTurnWork work)
    {
        var route = await work.Route(grains).GetAsync();
        var target = route.TargetAgentOverride ?? work.Connection.TargetAgentId;
        var agent = string.IsNullOrEmpty(target) ? null : await grains.GetGrain<IAgentRegistryGrain>(work.UserId).GetAsync(target);
        return string.Join('\n',
            $"Model: {route.ModelOverride ?? "your default model"}",
            $"Agent: {agent?.Name ?? "none (plain chat)"}",
            $"Messages in this conversation: {route.TurnCount} of {ChatGatewayLimits.MaxTurnsPerConversation}",
            $"Home chat: {(work.Connection.HomeRouteKey == work.RouteKey ? "this one" : work.Connection.HomeRouteKey is null ? "not set" : "another chat")}");
    }

    // `auto` when the user made Auto their default model and still has a router, else null (ADR 0139). A failure reads as no Auto
    // default: the turn then runs on the real default, as it did before.
    internal async Task<string?> AutoDefaultAsync(string userId)
    {
        try
        {
            if (!await HasRouterAsync(userId))
            {
                return null;
            }

            var stored = (await grains.GetGrain<IUserProviderConfigGrain>(userId).GetAsync()).Default;
            return AutoModelId.IsAuto(stored) ? AutoModelId.Value : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the Auto default for a chat turn");
            return null;
        }
    }

    private async Task<bool> HasRouterAsync(string userId)
    {
        try
        {
            return routers is not null && await routers.GetForUserAsync(userId) is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the model router for a chat command");
            return false;
        }
    }

    private async Task<string> SetAutoModelAsync(ChatTurnWork work, IChatRouteGrain route)
    {
        if (!await HasRouterAsync(work.UserId))
        {
            return "Auto isn't set up. Add a Decision-1 router provider in Agent42 first.";
        }

        await route.SetModelAsync(AutoModelId.Value);
        return "Model set to Auto: each message picks one of your models. Agent conversations keep their agent's model.";
    }

    private async Task<string> ListModelsAsync(string userId, IReadOnlyList<ModelInfo> models)
    {
        var lines = models.Take(MaxListedItems).Select(m => $"• {m.Id}").ToList();
        if (await HasRouterAsync(userId))
        {
            lines.Insert(0, $"• {AutoModelId.Value} (picks a model for each message)");
        }

        return lines.Count == 0
            ? "You have no models yet. Add a model provider in Agent42."
            : "Models:\n" + string.Join('\n', lines);
    }

    private async Task<IReadOnlyList<ModelInfo>?> ModelsAsync(string userId)
    {
        if (modelCatalogs is null)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(CatalogTimeout);
            var catalog = await modelCatalogs.GetForUserAsync(userId, timeout.Token);
            return (await catalog.GetAsync(timeout.Token)).Models;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            logger.LogWarning(ex, "Listing models for a chat command failed.");
            return null;
        }
    }
}
