using Agent42.Agents;

namespace Agent42.Backend;

public sealed partial class ChatTurnPipeline
{
    private const int MaxListedItems = 30;
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(10);

    // Name → handler, never an if-chain (Hermes' dispatch table). Each returns the reply text.
    private IReadOnlyDictionary<string, Func<ChatTurnWork, string, Task<string>>> CommandHandlers => field ??= new Dictionary<string, Func<ChatTurnWork, string, Task<string>>>(StringComparer.Ordinal)
    {
        [ChatCommands.New] = NewAsync,
        [ChatCommands.Stop] = StopAsync,
        [ChatCommands.Model] = ModelAsync,
        [ChatCommands.Agent] = AgentAsync,
        [ChatCommands.SetHome] = SetHomeAsync,
        [ChatCommands.Status] = StatusAsync,
        [ChatCommands.Help] = (work, _) => Task.FromResult(ChatCommands.HelpText(Capabilities(work.Connection).CommandPrefix)),
    };

    private async Task RunCommandAsync(ChatTurnWork work, ChatCommand command)
    {
        string reply;
        try
        {
            reply = await CommandHandlers[command.Name](work, command.Arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat command {Command} failed.", command.Name);
            reply = "That command failed. Try again in a moment.";
        }

        await ReplyAsync(work, reply);
    }

    private IChatRouteGrain Route(ChatTurnWork work)
        => _grains.GetGrain<IChatRouteGrain>(ChatRouteKey.GrainKey(work.UserId, work.Connection.Id, work.RouteKey));

    private async Task<string> NewAsync(ChatTurnWork work, string arguments)
    {
        await Route(work).StartNewAsync();
        return "Started a new conversation.";
    }

    private async Task<string> StopAsync(ChatTurnWork work, string arguments)
    {
        var dropped = ClearWaiting(work.QueueKey);
        if ((await Route(work).GetAsync()).ConversationId is { } conversationId)
        {
            await _grains.GetGrain<IAgentGrain>($"{work.UserId}/{conversationId}").StopAsync();
        }

        return dropped == 0 ? "Stopped." : $"Stopped, and dropped {dropped} waiting message{(dropped == 1 ? "" : "s")}.";
    }

    private async Task<string> ModelAsync(ChatTurnWork work, string arguments)
    {
        var route = Route(work);
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
            return $"There's no model called \"{arguments}\". {Capabilities(work.Connection).CommandPrefix}model list shows yours.";
        }

        await route.SetModelAsync(match.Id);
        return $"Model set to {match.Id}. Agent conversations keep their agent's model.";
    }

    private async Task<bool> HasRouterAsync(string userId)
    {
        try
        {
            return _routers is not null && await _routers.GetForUserAsync(userId) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the model router for a chat command");
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

    private async Task<string> AgentAsync(ChatTurnWork work, string arguments)
    {
        var route = Route(work);
        var registry = _grains.GetGrain<IAgentRegistryGrain>(work.UserId);
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
            return $"There's no agent called \"{arguments}\". {Capabilities(work.Connection).CommandPrefix}agent list shows yours.";
        }

        await route.SetAgentAsync(chosen.Id);
        return $"Started a new conversation with {chosen.Name}.";
    }

    private async Task<string> SetHomeAsync(ChatTurnWork work, string arguments)
    {
        await _grains.GetGrain<IUserChatConnectionsGrain>(work.UserId).SetHomeRouteAsync(work.Connection.Id, work.RouteKey);
        return work.Connection.NotificationsEnabled
            ? "Notifications and scheduled results will come to this chat."
            : "This chat is now home. Turn on notifications for this connection in Agent42 to receive them here.";
    }

    private async Task<string> StatusAsync(ChatTurnWork work, string arguments)
    {
        var route = await Route(work).GetAsync();
        var target = route.TargetAgentOverride ?? work.Connection.TargetAgentId;
        var agent = string.IsNullOrEmpty(target) ? null : await _grains.GetGrain<IAgentRegistryGrain>(work.UserId).GetAsync(target);
        return string.Join('\n',
            $"Model: {route.ModelOverride ?? "your default model"}",
            $"Agent: {agent?.Name ?? "none (plain chat)"}",
            $"Messages in this conversation: {route.TurnCount} of {ChatGatewayLimits.MaxTurnsPerConversation}",
            $"Home chat: {(work.Connection.HomeRouteKey == work.RouteKey ? "this one" : work.Connection.HomeRouteKey is null ? "not set" : "another chat")}");
    }

    private async Task<IReadOnlyList<ModelInfo>?> ModelsAsync(string userId)
    {
        if (_modelCatalogs is null)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(CatalogTimeout);
            var catalog = await _modelCatalogs.GetForUserAsync(userId, timeout.Token);
            return (await catalog.GetAsync(timeout.Token)).Models;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Listing models for a chat command failed.");
            return null;
        }
    }
}
