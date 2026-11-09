using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>One analyzer turn from the panel: the text and the model it picked. Both are required.</summary>
public sealed record FleetAnalyzerRequest(string? Message, string? Model);

/// <summary>
/// The analyzer panel on <c>/fleet/</c> (ADR 0123): one persisted <see cref="AgentKinds.FleetAnalyzer"/> conversation per
/// task, <c>fleet-analyzer-{taskId}</c> under the caller's own grain key. It is never touched into the conversation index
/// (so no sidebar) and its turns are never search-indexed. Every route is behind Easy Auth and scoped to
/// <c>GetUserId()</c>; the POST and GET check that the task is the caller's before any grain is addressed.
/// </summary>
public static partial class AgentApi
{
    private static void MapFleetAnalyzerEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/fleet/tasks/{id}/analyzer", FleetAnalyzerChatAsync);
        app.MapGet("/api/fleet/tasks/{id}/analyzer", FleetAnalyzerHistoryAsync);
        app.MapDelete("/api/fleet/tasks/{id}/analyzer", ClearFleetAnalyzerAsync);
    }

    private static IAgentGrain FleetAnalyzerGrain(IGrainFactory grains, string userId, string taskId)
        => grains.GetGrain<IAgentGrain>(AgentKey(userId, FleetAnalyzerDefaults.ConversationId(taskId)));

    private static async Task FleetAnalyzerChatAsync(
        string id, FleetAnalyzerRequest request, IServiceProvider services, IGrainFactory grains, HttpContext http)
    {
        var userId = http.GetUserId();
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            await FleetUnavailable().ExecuteAsync(http);
            return;
        }

        if (await fleet.GetAsync(userId, id, http.RequestAborted) is null)
        {
            await FleetNotFound().ExecuteAsync(http);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Message) || string.IsNullOrWhiteSpace(request.Model))
        {
            await FleetBadRequest("Send a message and pick a model.").ExecuteAsync(http);
            return;
        }

        if (await FleetAnalyzerModelRefusalAsync(services, userId, request.Model, http.RequestAborted) is { } refusal)
        {
            await FleetBadRequest(refusal).ExecuteAsync(http);
            return;
        }

        // Bound once (idempotent); the model is passed on every turn, so the panel's picker keeps working.
        var agent = FleetAnalyzerGrain(grains, userId, id);
        await agent.BindAgentAsync(new AgentBinding(
            AgentKinds.FleetAnalyzer, FleetAnalyzerDefaults.Name, FleetAnalyzerDefaults.SystemPrompt,
            request.Model, null, AgentKinds.FleetAnalyzer));

        http.Response.ContentType = "application/x-ndjson";
        await foreach (var evt in agent.ChatAsync(
            FleetAnalyzerDefaults.OpenTaskPrefix(id) + request.Message, request.Model, clientTools: []))
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(evt, ChatStreamJson.Options) + "\n");
            await http.Response.Body.FlushAsync();
        }
    }

    // The analyzer is a plain ChatClientAgent with function tools, so a model that takes none, or one that only a
    // Copilot agent may use (CopilotOnlyChatClient refuses it), would fail every turn: refuse it up front.
    private static async Task<string?> FleetAnalyzerModelRefusalAsync(
        IServiceProvider services, string userId, string model, CancellationToken ct)
    {
        if (services.GetService<IUserModelCatalogProvider>() is not { } catalogs)
        {
            return null;
        }

        var models = (await (await catalogs.GetForUserAsync(userId, ct)).GetAsync(ct)).Models;
        return models.FirstOrDefault(m => m.Id == model) switch
        {
            null => "That model is not available to you.",
            { SupportsTools: false } => "The analyzer needs a model that supports tools.",
            { CopilotOnly: true } => "That model only works in Copilot agents.",
            _ => null,
        };
    }

    private static async Task<IResult> FleetAnalyzerHistoryAsync(
        string id, IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct)
    {
        var userId = http.GetUserId();
        if (services.GetService<FleetTaskService>() is not { } fleet)
        {
            return FleetUnavailable();
        }

        if (await fleet.GetAsync(userId, id, ct) is null)
        {
            return FleetNotFound();
        }

        // The open-task prefix is persisted with the user's text (the model needs it on replay), so it is cut here.
        var prefix = FleetAnalyzerDefaults.OpenTaskPrefix(id);
        var turns = await FleetAnalyzerGrain(grains, userId, id).GetHistoryAsync();
        return Results.Json(turns.Select(t => t.Role == "user" && t.Text.StartsWith(prefix, StringComparison.Ordinal)
            ? t with { Text = t.Text[prefix.Length..] }
            : t));
    }

    // No task check, on purpose: the key is the caller's own, so this can only ever clear the caller's conversation, and a
    // task that was deleted must still be clearable. A turn in flight is stopped first so it cannot write the history back.
    private static async Task<IResult> ClearFleetAnalyzerAsync(string id, IGrainFactory grains, HttpContext http)
    {
        var agent = FleetAnalyzerGrain(grains, http.GetUserId(), id);
        await agent.StopAsync();
        await agent.PurgeAsync();
        return Results.NoContent();
    }
}
