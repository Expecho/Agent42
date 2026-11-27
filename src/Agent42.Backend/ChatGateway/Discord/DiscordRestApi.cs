using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Agent42.Backend;

/// <summary>One REST answer: the body on success, otherwise the status, Discord's JSON error code and any wait.</summary>
public sealed record DiscordResponse(int Status, JsonElement Body, int ErrorCode, TimeSpan? RetryAfter)
{
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>The send outcome in the gateway's terms: the streamer reacts to the kind, never to Discord's text.</summary>
    public ChatSendResult ToSendResult()
    {
        if (Ok)
        {
            return ChatSendResult.Ok(Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("id", out var id) ? id.GetString() : null);
        }

        return Status switch
        {
            429 => ChatSendResult.Fail(ChatSendErrorKind.RateLimited, RetryAfter),
            // 50035 "Invalid Form Body" is how an over-long content is refused.
            400 when ErrorCode == 50035 => ChatSendResult.Fail(ChatSendErrorKind.TooLong),
            403 => ChatSendResult.Fail(ChatSendErrorKind.Forbidden),
            404 => ChatSendResult.Fail(ChatSendErrorKind.NotFound),
            >= 500 or 0 => ChatSendResult.Fail(ChatSendErrorKind.Transient),
            _ => ChatSendResult.Fail(ChatSendErrorKind.Unknown),
        };
    }
}

/// <summary>
/// The handful of Discord REST calls the gateway makes (ADR 0111), hand-rolled. Discord.Net holds only the gateway
/// websocket, the part C10 says not to hand-roll. Its REST client is not used for replies: by default it retries and
/// waits out rate-limit buckets inside the library, which is the inline sleep the streamer forbids (a 429 must come
/// back as <see cref="ChatSendErrorKind.RateLimited"/>), and it would hide the mention rule below behind its defaults.
///
/// <para><b>Every message goes out with <c>allowed_mentions: {"parse": []}</c></b>, so no reply can ever ping
/// <c>@everyone</c>, <c>@here</c>, a role or a user, whatever the model writes (Hermes <c>discord/adapter.py</c>).
/// <c>DiscordRestApi_SendsWithoutMentions</c> pins it.</para>
///
/// <para>The token is a header, never in the URL. The named client drops the inherited retry pipeline (CLAUDE.md
/// rule 8: a retried create that timed out after Discord accepted it posts the reply twice) and has an explicit
/// timeout.</para>
/// </summary>
public sealed class DiscordRestApi(IHttpClientFactory httpClients, DiscordOptions options)
{
    /// <summary>Named client for REST calls.</summary>
    public const string HttpClientName = "discord";

    /// <summary>Named client for attachment downloads: no token, no redirects.</summary>
    public const string CdnHttpClientName = "discord-cdn";

    /// <summary>Where Discord serves attachments (<c>url</c> and <c>proxy_url</c>). Nothing else is ever fetched.</summary>
    public static readonly IReadOnlySet<string> CdnHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cdn.discordapp.com", "media.discordapp.net" };

    private static readonly object NoMentions = new { parse = Array.Empty<string>() };

    public static void AddHttpClients(IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(30))
            .WithoutInheritedRetry();
        services.AddHttpClient(CdnHttpClientName, http => http.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .WithoutInheritedRetry();
    }

    /// <summary>The bot's own user (<c>GET /users/@me</c>), which validates the token.</summary>
    public Task<DiscordResponse> GetMeAsync(string token, CancellationToken cancellationToken)
        => SendAsync(token, HttpMethod.Get, "users/@me", null, cancellationToken);

    /// <summary>
    /// Posts <paramref name="content"/> in <paramref name="channelId"/>, as a reply to <paramref name="replyTo"/> when
    /// given (a reply whose original is gone is sent plainly rather than refused).
    /// </summary>
    public Task<DiscordResponse> CreateMessageAsync(string token, string channelId, string content, string? replyTo, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["content"] = content, ["allowed_mentions"] = NoMentions };
        if (replyTo is not null)
        {
            body["message_reference"] = new { message_id = replyTo, fail_if_not_exists = false };
        }

        return SendAsync(token, HttpMethod.Post, $"channels/{channelId}/messages", body, cancellationToken);
    }

    /// <summary>
    /// Posts one file as a message (multipart: <c>payload_json</c> plus <c>files[0]</c>), with the same
    /// <c>allowed_mentions</c> rule as every other message.
    /// </summary>
    public async Task<DiscordResponse> CreateMessageWithFileAsync(
        string token, string channelId, DiscordUploadFile upload, string? replyTo, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["allowed_mentions"] = NoMentions,
            ["attachments"] = new[] { new { id = 0, filename = upload.FileName } },
        };
        if (replyTo is not null)
        {
            payload["message_reference"] = new { message_id = replyTo, fail_if_not_exists = false };
        }

        var form = new MultipartFormDataContent { { JsonContent.Create(payload), "payload_json" } };
        var file = new ByteArrayContent(upload.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(upload.MediaType);
        form.Add(file, "files[0]", upload.FileName);
        return await SendAsync(token, HttpMethod.Post, $"channels/{channelId}/messages", form, cancellationToken);
    }

    public Task<DiscordResponse> EditMessageAsync(string token, string channelId, string messageId, string content, CancellationToken cancellationToken)
        => SendAsync(token, HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}",
            new Dictionary<string, object> { ["content"] = content, ["allowed_mentions"] = NoMentions }, cancellationToken);

    public Task<DiscordResponse> TriggerTypingAsync(string token, string channelId, CancellationToken cancellationToken)
        => SendAsync(token, HttpMethod.Post, $"channels/{channelId}/typing", null, cancellationToken);

    /// <summary>Starts a thread on <paramref name="messageId"/>; Discord gives the thread the message's own id.</summary>
    public Task<DiscordResponse> StartThreadAsync(string token, string channelId, string messageId, string name, CancellationToken cancellationToken)
        => SendAsync(token, HttpMethod.Post, $"channels/{channelId}/messages/{messageId}/threads",
            new { name, auto_archive_duration = 1440 }, cancellationToken);

    /// <summary>
    /// Downloads an attachment, capped at <paramref name="maxBytes"/>. <c>null</c> when the URL is not HTTPS on one of
    /// <see cref="CdnHosts"/> (or the configured stand-in), when it redirects, or when it is too large. No token is sent.
    /// </summary>
    public async Task<byte[]?> DownloadAttachmentAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        if (!IsPinnedAttachmentUrl(url, options.CdnBaseUrl))
        {
            return null;
        }

        var client = httpClients.CreateClient(CdnHttpClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.IsSuccessStatusCode
            ? await BoundedResponseReader.ReadAsync(response.Content, maxBytes, cancellationToken)
            : null;
    }

    public static bool IsPinnedAttachmentUrl(string url, Uri? standIn)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return standIn is not null
            ? uri.Scheme == standIn.Scheme && uri.Host == standIn.Host && uri.Port == standIn.Port
            : uri.Scheme == Uri.UriSchemeHttps && CdnHosts.Contains(uri.Host) && uri.IsDefaultPort;
    }

    private async Task<DiscordResponse> SendAsync(string token, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(options.ApiBaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        if (body is not null)
        {
            request.Content = body as HttpContent ?? JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var json = response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            : default;
        return new DiscordResponse((int)response.StatusCode, json, ErrorCode(json), RetryAfter(response, json));
    }

    private static int ErrorCode(JsonElement json)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : 0;

    // A 429's body carries retry_after in (fractional) seconds; the Retry-After header is the fallback.
    private static TimeSpan? RetryAfter(HttpResponseMessage response, JsonElement json)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return null;
        }

        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("retry_after", out var seconds) && seconds.ValueKind == JsonValueKind.Number)
        {
            return TimeSpan.FromSeconds(seconds.GetDouble());
        }

        return response.Headers.RetryAfter?.Delta
            ?? (response.Headers.TryGetValues("Retry-After", out var values)
                && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var header)
                    ? TimeSpan.FromSeconds(header)
                    : null);
    }
}

/// <summary>The file part of a Discord multipart message.</summary>
public sealed record DiscordUploadFile(byte[] Bytes, string FileName, string MediaType);

/// <summary>Where Discord lives. Only DevHost and tests point it at local stand-ins.</summary>
/// <param name="ApiBaseUrl">The REST base, ending in <c>/</c>.</param>
/// <param name="GatewayUrl">
/// A fixed gateway websocket URL (Discord.Net's <c>GatewayHost</c>). <c>null</c> in production, where the library asks
/// <c>/gateway/bot</c>. When set, connecting makes no REST call at all.
/// </param>
/// <param name="CdnBaseUrl">A stand-in attachment host that replaces the CDN pin. <c>null</c> in production.</param>
public sealed record DiscordOptions(Uri ApiBaseUrl, string? GatewayUrl = null, Uri? CdnBaseUrl = null)
{
    public static readonly DiscordOptions Default = new(new Uri("https://discord.com/api/v10/"));
}
