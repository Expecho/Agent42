using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

public sealed partial class ChatTurnPipeline
{
    private const string FailureText = "I couldn't answer that. Check your model provider in Agent42 and try again.";
    private const string MissingAgentText = "This chat is set to talk to an agent that no longer exists. Pick another with {0}agent, or {0}agent none for plain chat.";
    private const string RolloverText = "This conversation got long, so I started a new one. The earlier one is in your Agent42 chat list.";
    private const string LongTurnText = "This is taking a while. I'm still working on it, and the reply will appear in this conversation in your Agent42 chat list.";
    private const int MaxTitleChars = 80;

    private async Task RunTurnSafelyAsync(ChatTurnWork work)
    {
        try
        {
            await RunTurnAsync(work);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A chat turn on connection {ConnectionId} failed before it could run.", work.Connection.Id);
            await ReplyAsync(work, FailureText);
        }
    }

    private async Task RunTurnAsync(ChatTurnWork work)
    {
        // A chat turn has no HTTP request, so set the owner's identity on this async scope, or every span of the turn
        // reaches /usage and /logs unattributed (gotcha "User identity reaches downstream/grain spans via Orleans
        // RequestContext").
        RequestContext.Set(UserTelemetryContext.RequestContextKey,
            work.Connection.OwnerTelemetry ?? new UserTelemetryContext(work.UserId, null, null, null, null, null));

        var voice = await _voice.TranscribeVoiceNoteAsync(work);
        if (voice.Reply is not null)
        {
            await ReplyAsync(work, voice.Reply);
        }

        if (!voice.Proceed)
        {
            return;
        }

        work = work with { Transcript = voice.Transcript };
        var files = ChatInboundFiles.AttachableFiles(work.Message);
        if (PromptText(work).Length == 0 && !ChatInboundFiles.AnyFileLooksUsable(files))
        {
            // Nothing a turn could read: say why, and mint no conversation for it.
            await ReplyAsync(work, files.Count == 0 ? TextOnlyText : ChatInboundFiles.SkippedNotice(
                files.Select(f => ChatInboundFiles.FileError(f)!).ToList()));
            return;
        }

        var route = work.Route(_grains);
        var index = _grains.GetGrain<IConversationIndexGrain>(work.UserId);
        var current = (await route.GetAsync()).ConversationId;
        // A conversation the owner deleted in the web UI starts fresh rather than resurrecting a hidden history.
        var deleted = current is not null && (await index.GetConversationsAsync()).All(c => c.Id != current);
        var turn = await route.BeginTurnAsync(forceNew: deleted);

        var title = Title(work);
        if (turn.Minted)
        {
            if (!await StartConversationAsync(work, turn, index, title))
            {
                return;
            }
        }
        else
        {
            await index.TouchAsync(turn.ConversationId, title);
        }

        var ownerKey = $"{work.UserId}/{turn.ConversationId}";
        var agentGrain = _grains.GetGrain<IAgentGrain>(ownerKey);
        var binding = await agentGrain.GetAgentBindingAsync();
        var prepared = await PrepareTurnFilesAsync(work, files, ownerKey, agentGrain, binding);
        var prompt = ChatInboundFiles.PromptWithFiles(PromptText(work), files, prepared);
        if (prompt.Length == 0 || (PromptText(work).Length == 0 && !prepared.HasUsable))
        {
            // Every file failed after all (a download, an ingest), and there is no text: the notice was the answer.
            return;
        }

        // An agent conversation runs on the agent's own model; /model applies to plain chat only (the web UI's rule).
        var model = binding is null ? turn.ModelOverride ?? await _modelCommands.AutoDefaultAsync(work.UserId) : null;
        var buffer = new ChatReplyBuffer();
        // Detached and never awaited past the cap: the turn runs to its persist whatever the chat does.
        _ = Task.Run(() => DrainAsync(agentGrain, prompt, model, prepared.Images, buffer));
        using var cap = new CancellationTokenSource();
        _ = Task.Delay(_maxTurnWait, _timeProvider, cap.Token)
            .ContinueWith(t => buffer.Fail(LongTurnText), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        await _streamer.StreamAsync(Destination(work) with { UploadImage = _files.ImageUploader(work, ownerKey) }, buffer, CancellationToken.None);
        await cap.CancelAsync();
    }

    // Stores the files, registers the documents BEFORE the turn (a mid-run ingest cannot rebuild the agent), and tells
    // the owner what was skipped. A copilot agent has no document retrieval, so a document is refused for one.
    private async Task<ChatTurnFiles> PrepareTurnFilesAsync(
        ChatTurnWork work, List<ChatInboundFile> files, string ownerKey, IAgentGrain agentGrain, AgentBinding? binding)
    {
        var prepared = await _files.PrepareAsync(work, files, ownerKey, AgentKinds.IsCopilot(binding?.Kind));
        foreach (var document in prepared.Documents)
        {
            await agentGrain.RegisterDocumentAsync(document);
        }

        if (prepared.Skipped.Count > 0)
        {
            await ReplyAsync(work, ChatInboundFiles.SkippedNotice(prepared.Skipped));
        }

        return prepared;
    }

    // Lists the new conversation and binds its agent, if any. False when the turn must not run.
    private async Task<bool> StartConversationAsync(ChatTurnWork work, ChatRouteTurn turn, IConversationIndexGrain index, string title)
    {
        var agentId = turn.TargetAgentOverride switch
        {
            null => work.Connection.TargetAgentId,
            "" => null,
            var chosen => chosen,
        };
        AgentDefinition? agent = null;
        if (agentId is not null
            && (agent = await _grains.GetGrain<IAgentRegistryGrain>(work.UserId).GetAsync(agentId)) is null)
        {
            await ReplyAsync(work, string.Format(MissingAgentText, Capabilities(work.Connection).CommandPrefix));
            return false;
        }

        if (turn.RolledOver)
        {
            await ReplyAsync(work, RolloverText);
        }

        await index.TouchAsync(turn.ConversationId, title, agent?.Id, agent?.Name);
        // The chat this conversation came from (C9), written once on mint and before the first ChatAsync, so a reminder
        // or queued run in it can deliver back here. A continued conversation never reaches this method.
        await _grains.GetGrain<IAgentGrain>($"{work.UserId}/{turn.ConversationId}").SetChatOriginAsync(
            new ChatOrigin { ConnectionId = work.Connection.Id, RouteKey = work.RouteKey, IsGroup = work.Message.ChatType == ChatType.Group });
        if (agent is not null)
        {
            // Bound only here, on a conversation the route just minted: binding under an existing history would rewrite
            // the persona beneath it (ADR 0003).
            await _grains.GetGrain<IAgentGrain>($"{work.UserId}/{turn.ConversationId}").BindAgentAsync(new AgentBinding(
                agent.Id, agent.Name, agent.SystemPrompt, agent.Model, agent.EnabledTools, agent.Kind,
                agent.RetentionMinutes));
        }

        return true;
    }

    // Enumerates the turn to completion whatever the platform does. interactive: false — a chat app cannot answer a
    // client tool, an MCP elicitation or a suggestion; on an interactive turn those pause the stream waiting for a
    // browser that is not there. ephemeral: false — a chat conversation is listed and delivers, never incognito.
    private async Task DrainAsync(
        IAgentGrain agentGrain, string prompt, string? model, List<AttachmentRef> images, ChatReplyBuffer buffer)
    {
        try
        {
            await foreach (var evt in agentGrain.ChatAsync(
                prompt, model, attachments: images.Count > 0 ? images : null, ephemeral: false, interactive: false))
            {
                buffer.Apply(evt);
            }

            buffer.Complete();
        }
        catch (Exception ex)
        {
            // Curated, never the exception (the AgentMcpRunner discipline): a provider's message or stack must not
            // reach a third-party chat.
            _logger.LogError(ex, "A chat turn failed in the model call.");
            buffer.Fail(FailureText);
        }
    }

    // The message the model sees: in a group, without the "@bot" that addressed it.
    private static string PromptText(ChatTurnWork work)
    {
        var text = work.Message.Text;
        if (work.Message.ChatType == ChatType.Group && work.Connection.BotUserName is { Length: > 0 } bot)
        {
            text = text.Replace("@" + bot, "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        return ChatVoiceTranscription.WithTranscript(text.Trim(), work.Transcript);
    }

    private static string Title(ChatTurnWork work)
    {
        // A voice note is titled by what was said, without the marker the model sees.
        var firstLine = (work.Transcript ?? PromptText(work)).Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0 && work.Message.Files.FirstOrDefault(f => !f.IsVoiceNote) is { } file)
        {
            firstLine = ChatInboundFiles.FileName(file);
        }

        return firstLine.Length == 0
            ? $"{work.Connection.Platform} chat"
            : firstLine.Length <= MaxTitleChars ? firstLine : firstLine[..MaxTitleChars];
    }
}
