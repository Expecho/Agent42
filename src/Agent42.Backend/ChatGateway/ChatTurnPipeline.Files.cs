using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Inbound files and outbound images (ADR 0111, phase 1c). Inbound files reuse the agent-run core (old D7): images
/// become attachments that ride the turn, documents are ingested and registered BEFORE the turn (gotcha "A mid-run
/// ingest cannot rebuild the agent").
///
/// <para><b>Skip and note, not all-or-nothing.</b> The agent-run door rejects a whole batch over one bad file, because
/// automation retries cleanly. A chat user cannot easily resend a mixed batch, so here, as for inbound email, a file
/// that cannot ride is skipped, the owner is told which and why, and the model is told too, so it never pretends to
/// have read it.</para>
///
/// <para>Every file is checked from its <b>metadata</b> (name, declared type, size) before anything is downloaded
/// (old C8), and downloaded only through the connection's adapter, which pins the platform's own host and caps the
/// read.</para>
/// </summary>
public sealed partial class ChatTurnPipeline
{
    /// <summary>Files one message may carry into a turn; Slack and Discord allow ten.</summary>
    public const int MaxFilesPerMessage = 10;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The files of one message, made ready for its turn.</summary>
    private sealed record ChatTurnFiles(List<AttachmentRef> Images, List<string> Documents, List<string> Skipped)
    {
        public static ChatTurnFiles None => new([], [], []);

        public bool HasUsable => Images.Count > 0 || Documents.Count > 0;
    }

    // The files a turn would try: every attachment but a voice note (those are transcribed, not attached).
    private static List<ChatInboundFile> AttachableFiles(ChatInboundMessage message)
        => message.Files.Where(f => !f.IsVoiceNote).ToList();

    private static string FileName(ChatInboundFile file)
        => string.IsNullOrWhiteSpace(file.FileName) ? "file" : Path.GetFileName(file.FileName);

    // Whether any file could ride a turn, judged from metadata alone. Lets a message with no text and only unusable
    // files be answered without minting a conversation.
    private static bool AnyFileLooksUsable(IEnumerable<ChatInboundFile> files)
        => files.Any(f => AgentApi.AgentRunFileError(FileName(f), f.DeclaredMediaType, f.Size, copilot: false) is null);

    // Validates, downloads and stores each file under the conversation's owner key. Never throws: a file that fails any
    // step is skipped with a reason the owner can read.
    private async Task<ChatTurnFiles> PrepareFilesAsync(ChatTurnWork work, List<ChatInboundFile> files, string ownerKey, bool copilot)
    {
        if (files.Count == 0)
        {
            return ChatTurnFiles.None;
        }

        var result = ChatTurnFiles.None;
        if (_attachments is null || _ingestion is null)
        {
            result.Skipped.Add("Files can't be read on this server.");
            return result;
        }

        foreach (var file in files.Take(MaxFilesPerMessage))
        {
            await PrepareFileAsync(work, file, ownerKey, copilot, result);
        }

        if (files.Count > MaxFilesPerMessage)
        {
            result.Skipped.Add($"Only the first {MaxFilesPerMessage} files were read.");
        }

        return result;
    }

    private async Task PrepareFileAsync(ChatTurnWork work, ChatInboundFile file, string ownerKey, bool copilot, ChatTurnFiles result)
    {
        var name = FileName(file);
        if (AgentApi.AgentRunFileError(name, file.DeclaredMediaType, file.Size, copilot) is { } error)
        {
            result.Skipped.Add(error);
            return;
        }

        if (await DownloadAsync(work, file) is not { } downloaded)
        {
            result.Skipped.Add($"'{name}' couldn't be downloaded. It may be over the {AgentApi.MaxAttachmentBytes / (1024 * 1024)} MB limit.");
            return;
        }

        try
        {
            var (images, documents, storeError) = await AgentApi.StoreAgentRunFilesAsync(
                [new AgentRunFileContent(name, file.DeclaredMediaType ?? downloaded.MediaType, downloaded.Bytes)],
                _attachments!, _ingestion!, ownerKey);
            if (storeError is not null)
            {
                // The ingest error names the owner's embedding setup; it is ours, but keep the chat text fixed anyway.
                result.Skipped.Add($"'{name}' couldn't be read for search. Check your embedding model in Agent42.");
                return;
            }

            result.Images.AddRange(images);
            result.Documents.AddRange(documents);
            if (images.Count == 0 && documents.Count == 0)
            {
                result.Skipped.Add($"'{name}' has no text that could be read.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A chat file on connection {ConnectionId} could not be stored.", work.Connection.Id);
            result.Skipped.Add($"'{name}' couldn't be stored.");
        }
    }

    // Through the connection's CURRENT session (host-pinned and capped by the adapter), time-boxed, never throwing.
    private async Task<ChatDownloadedFile?> DownloadAsync(ChatTurnWork work, ChatInboundFile file)
    {
        if (_sessions.Get(work.Connection.Id) is not { } session)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout, _timeProvider);
            return await session.DownloadAsync(file, AgentApi.MaxAttachmentBytes, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A chat file download on connection {ConnectionId} failed.", work.Connection.Id);
            return null;
        }
    }

    // The prompt the model sees: the owner's words, what was skipped (so the model never claims to have read it), and,
    // for a files-only message, what was sent.
    private static string PromptWithFiles(string text, IReadOnlyList<ChatInboundFile> files, ChatTurnFiles prepared)
    {
        var prompt = text;
        if (prompt.Length == 0 && prepared.HasUsable)
        {
            prompt = "The user sent " + string.Join(", ", files.Select(f => $"'{FileName(f)}'")) + ".";
        }

        foreach (var reason in prepared.Skipped)
        {
            prompt += $"\n[Attachment skipped: {reason}]";
        }

        return prompt.Trim();
    }

    private static string SkippedNotice(IReadOnlyList<string> skipped)
        => skipped.Count == 1 ? skipped[0] : "Some files were skipped:\n" + string.Join('\n', skipped.Select(s => "• " + s));

    // Uploads one generated image from the conversation's store through the session the streamer is using.
    private Func<IChatPlatformSession, string, CancellationToken, Task<ChatSendResult>>? ImageUploader(ChatTurnWork work, string ownerKey)
    {
        if (_attachments is not { } attachments)
        {
            return null;
        }

        return async (session, attachmentId, cancellationToken) =>
            await attachments.OpenReadAsync(ownerKey, attachmentId, cancellationToken) is { } content
                ? await session.UploadImageAsync(work.Target, content.Bytes, content.FileName, content.MediaType, cancellationToken)
                : ChatSendResult.Fail(ChatSendErrorKind.NotFound);
    }
}
