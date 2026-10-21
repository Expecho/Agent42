using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Agent42.Backend;

/// <summary>One Web API answer: the whole body when <c>ok</c>, otherwise Slack's <c>error</c> string.</summary>
public sealed record SlackResponse(bool Ok, JsonElement Body, string? Error, int Status, TimeSpan? RetryAfter)
{
    /// <summary>
    /// Whether the token itself is dead: the connection is faulted rather than retried (ADR 0111). Slack answers these
    /// as HTTP 200 with <c>ok: false</c>, so the status code alone says nothing.
    /// </summary>
    public bool IsAuthFailure => Error is "invalid_auth" or "not_authed" or "account_inactive" or "token_revoked"
        or "token_expired" or "not_allowed_token_type" or "missing_scope";

    /// <summary>The send outcome in the gateway's terms: the streamer reacts to the kind, never to Slack's error text.</summary>
    public ChatSendResult ToSendResult()
    {
        if (Ok)
        {
            return ChatSendResult.Ok(Body.TryGetProperty("ts", out var ts) ? ts.GetString() : null);
        }

        return Error switch
        {
            "ratelimited" or "rate_limited" => ChatSendResult.Fail(ChatSendErrorKind.RateLimited, RetryAfter),
            "msg_too_long" or "msg_blocks_too_long" => ChatSendResult.Fail(ChatSendErrorKind.TooLong),
            "invalid_blocks" or "invalid_blocks_format" => ChatSendResult.Fail(ChatSendErrorKind.BadFormat),
            "message_not_found" or "channel_not_found" or "thread_not_found" => ChatSendResult.Fail(ChatSendErrorKind.NotFound),
            "not_in_channel" or "is_archived" or "cant_update_message" or "edit_window_closed" or "restricted_action"
                => ChatSendResult.Fail(ChatSendErrorKind.Forbidden),
            _ when Status >= 500 || Status == 0 || Error is "internal_error" or "fatal_error" or "service_unavailable" or "request_timeout"
                => ChatSendResult.Fail(ChatSendErrorKind.Transient),
            _ => ChatSendResult.Fail(ChatSendErrorKind.Unknown),
        };
    }
}

/// <summary>
/// The few Slack Web API calls the gateway uses (ADR 0111), hand-rolled rather than an SDK: <c>auth.test</c>,
/// <c>apps.connections.open</c>, <c>chat.postMessage</c>, <c>chat.update</c> and a host-pinned file download.
///
/// <para>Tokens go in the <c>Authorization</c> header, never the URL, so neither spans nor <c>IHttpClientFactory</c>'s
/// request logs can carry one (unlike Telegram's path token). Both named clients drop the inherited retry pipeline
/// (CLAUDE.md rule 8: a retried <c>chat.postMessage</c> that timed out after Slack accepted it posts the reply twice)
/// and set an explicit timeout.</para>
/// </summary>
public sealed class SlackWebApi(IHttpClientFactory httpClients, SlackOptions options)
{
    /// <summary>Named client for Web API calls.</summary>
    public const string HttpClientName = "slack";

    /// <summary>Named client for file downloads: it never follows a redirect, so a token can't be carried off-host.</summary>
    public const string FilesHttpClientName = "slack-files";

    /// <summary>The only host an inbound file is ever fetched from, with the bot token (old C8).</summary>
    public const string FilesHost = "files.slack.com";

    public static void AddHttpClients(IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllResilienceHandlers();
        services.AddHttpClient(FilesHttpClientName, http => http.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    /// <summary>Calls <paramref name="method"/> with a JSON body (or none) under <paramref name="token"/>.</summary>
    public async Task<SlackResponse> CallAsync(string token, string method, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiBaseUrl, method));
        request.Content = JsonContent.Create(body ?? new { });
        return await SendAsync(token, request, cancellationToken);
    }

    /// <summary>
    /// Calls <paramref name="method"/> with form-encoded arguments. The external-upload methods take their arguments
    /// this way (a JSON body is not among the formats they document).
    /// </summary>
    public async Task<SlackResponse> CallFormAsync(
        string token, string method, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiBaseUrl, method));
        request.Content = new FormUrlEncodedContent(arguments);
        return await SendAsync(token, request, cancellationToken);
    }

    /// <summary>
    /// Shares <paramref name="bytes"/> as a file in a channel (and thread): <c>files.getUploadURLExternal</c>, the bytes
    /// to the returned URL, then <c>files.completeUploadExternal</c>. The upload URL is used only when it is on
    /// <see cref="FilesHost"/> (the same pin as downloads), so the token never goes anywhere else. Needs <c>files:write</c>.
    /// </summary>
    public async Task<SlackResponse> UploadFileAsync(
        string token, string channel, string? threadTs, byte[] bytes, string fileName, CancellationToken cancellationToken)
    {
        var ticket = await CallFormAsync(token, "files.getUploadURLExternal", new Dictionary<string, string>
        {
            ["filename"] = fileName,
            ["length"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, cancellationToken);
        if (!ticket.Ok)
        {
            return ticket;
        }

        var uploadUrl = ticket.Body.TryGetProperty("upload_url", out var url) ? url.GetString() : null;
        var fileId = ticket.Body.TryGetProperty("file_id", out var id) ? id.GetString() : null;
        if (uploadUrl is null || fileId is null || !IsPinnedFileUrl(uploadUrl, options.FilesBaseUrl))
        {
            return new SlackResponse(false, default, "upload_url_not_trusted", 200, null);
        }

        var client = httpClients.CreateClient(FilesHttpClientName);
        using (var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl))
        {
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            upload.Content = new ByteArrayContent(bytes);
            using var uploaded = await client.SendAsync(upload, cancellationToken);
            if (!uploaded.IsSuccessStatusCode)
            {
                return new SlackResponse(false, default, null, (int)uploaded.StatusCode, uploaded.Headers.RetryAfter?.Delta);
            }
        }

        var complete = new Dictionary<string, string>
        {
            ["files"] = JsonSerializer.Serialize(new[] { new { id = fileId, title = fileName } }),
            ["channel_id"] = channel,
        };
        if (threadTs is not null)
        {
            complete["thread_ts"] = threadTs;
        }

        return await CallFormAsync(token, "files.completeUploadExternal", complete, cancellationToken);
    }

    private async Task<SlackResponse> SendAsync(string token, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(HttpClientName);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (!response.IsSuccessStatusCode)
        {
            // A 429 has an empty or non-JSON body; Retry-After is the header Slack documents for it.
            var error = response.StatusCode == HttpStatusCode.TooManyRequests ? "ratelimited" : null;
            return new SlackResponse(false, default, error, (int)response.StatusCode, retryAfter);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var ok = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
        var errorText = !ok && json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() : null;
        return new SlackResponse(ok, json, errorText, (int)response.StatusCode, retryAfter);
    }

    /// <summary>
    /// Downloads a file's <c>url_private_download</c>, capped at <paramref name="maxBytes"/>. <c>null</c> when the URL
    /// is not on <see cref="FilesHost"/> over HTTPS (the token is never sent anywhere else), when Slack redirects, when
    /// the file is too large, or when Slack answers with an HTML page: that is its login page, which is how a missing
    /// <c>files:read</c> scope or a dead token looks (Slack does not answer 401 here).
    /// </summary>
    public async Task<byte[]?> DownloadFileAsync(string botToken, string url, long maxBytes, CancellationToken cancellationToken)
    {
        if (!IsPinnedFileUrl(url, options.FilesBaseUrl))
        {
            return null;
        }

        var client = httpClients.CreateClient(FilesHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentType?.MediaType == "text/html"
            || response.Content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadCappedAsync(stream, maxBytes, cancellationToken);
    }

    // HTTPS on files.slack.com only, or (DevHost and tests) exactly the configured stand-in's scheme, host and port.
    public static bool IsPinnedFileUrl(string url, Uri? standIn)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (standIn is not null)
        {
            return uri.Scheme == standIn.Scheme && uri.Host == standIn.Host && uri.Port == standIn.Port;
        }

        return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(FilesHost, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// Where the Slack Web API lives. Only DevHost and tests point it elsewhere (a local stand-in); the Socket Mode URL
/// always comes from <c>apps.connections.open</c>, so it follows the API base.
/// </summary>
/// <param name="ApiBaseUrl">The Web API base, ending in <c>/</c>.</param>
/// <param name="FilesBaseUrl">A stand-in file host that replaces the <c>files.slack.com</c> pin. <c>null</c> in production.</param>
public sealed record SlackOptions(Uri ApiBaseUrl, Uri? FilesBaseUrl = null)
{
    public static readonly SlackOptions Default = new(new Uri("https://slack.com/api/"));
}
