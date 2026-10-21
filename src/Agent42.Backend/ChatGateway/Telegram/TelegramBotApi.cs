using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry;

namespace Agent42.Backend;

/// <summary>One Bot API answer: its <c>result</c> when <c>ok</c>, otherwise the error the gateway reacts to.</summary>
public sealed record TelegramResponse(bool Ok, JsonElement Result, int ErrorCode, string? Description, TimeSpan? RetryAfter)
{
    /// <summary>The send outcome in the gateway's terms (ADR 0111): the streamer reacts to the kind, never the text.</summary>
    public ChatSendResult ToSendResult()
    {
        if (Ok)
        {
            return ChatSendResult.Ok(Result.ValueKind == JsonValueKind.Object && Result.TryGetProperty("message_id", out var id)
                ? id.GetRawText()
                : null);
        }

        var description = Description ?? "";
        return ErrorCode switch
        {
            429 => ChatSendResult.Fail(ChatSendErrorKind.RateLimited, RetryAfter),
            // Editing to identical text is not a failure for us: the message already shows it.
            400 when description.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) => ChatSendResult.Ok(),
            400 when description.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase) => ChatSendResult.Fail(ChatSendErrorKind.BadFormat),
            400 when description.Contains("too long", StringComparison.OrdinalIgnoreCase) => ChatSendResult.Fail(ChatSendErrorKind.TooLong),
            400 when description.Contains("not found", StringComparison.OrdinalIgnoreCase) => ChatSendResult.Fail(ChatSendErrorKind.NotFound),
            403 => ChatSendResult.Fail(ChatSendErrorKind.Forbidden),
            >= 500 or 0 => ChatSendResult.Fail(ChatSendErrorKind.Transient),
            _ => ChatSendResult.Fail(ChatSendErrorKind.Unknown),
        };
    }
}

/// <summary>
/// The handful of Telegram Bot API calls the gateway uses (ADR 0111), hand-rolled on two named clients rather than a
/// library: about ten JSON POSTs do not justify a dependency and its CVE surface.
///
/// <para>Three named clients, every one token-safe in the same way: ordinary calls, the long poll, and files.</para>
///
/// <para><b>The bot token is in the URL path</b> (<c>/bot&lt;token&gt;/sendMessage</c>; Telegram accepts it nowhere
/// else). OpenTelemetry redacts a URL's query string but never its path, so both clients run every call inside
/// <see cref="TelegramSpanSuppressingHandler"/> (no HttpClient span at all) and drop <c>IHttpClientFactory</c>'s
/// request logging, whose "Sending HTTP request …" lines carry the full URL into OTLP, App Insights and the
/// owner-visible <c>/logs</c> buffer (gotcha "OTel redacts an HttpClient span's query string by default"). This
/// class never logs a URL either. <c>TelegramBotApi_NeverLeaksTheToken</c> pins both channels.</para>
/// </summary>
public sealed class TelegramBotApi(IHttpClientFactory httpClients, TelegramOptions options)
{
    /// <summary>Named client for ordinary calls.</summary>
    public const string HttpClientName = "telegram";

    /// <summary>Named client for <c>getUpdates</c>, whose long poll outlasts an ordinary timeout.</summary>
    public const string PollHttpClientName = "telegram-poll";

    /// <summary>Named client for file downloads and uploads: a 10 MB file on a slow link outlasts an ordinary call.</summary>
    public const string FilesHttpClientName = "telegram-files";

    /// <summary>How long one <c>getUpdates</c> long poll waits for an update.</summary>
    public static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wires both named clients: span suppression, no request logging, no inherited retry (CLAUDE.md rule 8: a retried
    /// <c>sendMessage</c> that timed out after Telegram accepted it posts the reply twice), and explicit timeouts.
    /// </summary>
    public static void AddHttpClients(IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler(() => new TelegramSpanSuppressingHandler())
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();
        services.AddHttpClient(PollHttpClientName, http => http.Timeout = PollTimeout + TimeSpan.FromSeconds(20))
            .AddHttpMessageHandler(() => new TelegramSpanSuppressingHandler())
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();
        services.AddHttpClient(FilesHttpClientName, http => http.Timeout = TimeSpan.FromMinutes(2))
            .AddHttpMessageHandler(() => new TelegramSpanSuppressingHandler())
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    public Task<TelegramResponse> CallAsync(string token, string method, object? body, CancellationToken cancellationToken)
        => SendAsync(HttpClientName, token, method, body, cancellationToken);

    /// <summary>One long poll. <paramref name="offset"/> confirms every update below it.</summary>
    public Task<TelegramResponse> GetUpdatesAsync(string token, long offset, CancellationToken cancellationToken)
        => SendAsync(PollHttpClientName, token, "getUpdates", new
        {
            offset,
            timeout = (int)PollTimeout.TotalSeconds,
            allowed_updates = new[] { "message" },
        }, cancellationToken);

    /// <summary>Downloads a file by the <c>file_path</c> <c>getFile</c> returned, capped at <paramref name="maxBytes"/>.</summary>
    public async Task<byte[]?> DownloadFileAsync(string token, string filePath, long maxBytes, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(FilesHttpClientName);
        using var response = await client.GetAsync(
            Endpoint($"file/bot{token}/{filePath}"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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

    /// <summary>
    /// Calls <paramref name="method"/> as multipart form data, with one uploaded file under <paramref name="fileField"/>
    /// (<c>sendPhoto</c>'s <c>photo</c>). Telegram takes uploads no other way.
    /// </summary>
    public async Task<TelegramResponse> UploadAsync(
        string token, string method, IReadOnlyDictionary<string, string> fields, string fileField,
        byte[] bytes, string fileName, string mediaType, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        form.Add(file, fileField, fileName);

        var client = httpClients.CreateClient(FilesHttpClientName);
        using var response = await client.PostAsync(Endpoint($"bot{token}/{method}"), form, cancellationToken);
        return Parse(await ReadJsonAsync(response, cancellationToken), response.StatusCode);
    }

    private async Task<TelegramResponse> SendAsync(
        string clientName, string token, string method, object? body, CancellationToken cancellationToken)
    {
        var client = httpClients.CreateClient(clientName);
        using var response = await client.PostAsJsonAsync(Endpoint($"bot{token}/{method}"), body ?? new { }, cancellationToken);
        return Parse(await ReadJsonAsync(response, cancellationToken), response.StatusCode);
    }

    private static async Task<JsonObject?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);

    // The token contains a ':', so "bot123:abc/getMe" as a RELATIVE Uri parses as scheme "bot123:" and is refused.
    // "./" forces a path; the base URL always ends with '/'.
    private Uri Endpoint(string path) => new(options.BaseUrl, "./" + path);

    internal static TelegramResponse Parse(JsonObject? json, HttpStatusCode status)
    {
        if (json is null)
        {
            return new TelegramResponse(false, default, (int)status, null, null);
        }

        var ok = json["ok"]?.GetValue<bool>() == true;
        var result = json["result"] is { } node ? JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()) : default;
        var retryAfter = json["parameters"]?["retry_after"]?.GetValue<int>() is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var errorCode = json["error_code"]?.GetValue<int>() ?? (ok ? 0 : (int)status);
        return new TelegramResponse(ok, result, errorCode, json["description"]?.GetValue<string>(), retryAfter);
    }
}

/// <summary>Where the Bot API lives. Only tests point it elsewhere (a local server).</summary>
public sealed record TelegramOptions(Uri BaseUrl)
{
    public static readonly TelegramOptions Default = new(new Uri("https://api.telegram.org/"));
}

/// <summary>
/// Runs every Telegram call inside <see cref="SuppressInstrumentationScope"/>, so OpenTelemetry's HttpClient
/// instrumentation emits no span whose <c>url.full</c> would carry the bot token (the technique the
/// "OTel redacts an HttpClient span's query string" gotcha prescribes: suppress, never rewrite).
/// </summary>
public sealed class TelegramSpanSuppressingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var scope = SuppressInstrumentationScope.Begin();
        return await base.SendAsync(request, cancellationToken);
    }
}
