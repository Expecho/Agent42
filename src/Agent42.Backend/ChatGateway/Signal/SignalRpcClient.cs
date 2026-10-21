using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Agent42.Backend;

/// <summary>An account the daemon holds: its E.164 number and its ACI (the bot's Signal user id).</summary>
public sealed record SignalAccount(string Number, string? Aci);

/// <summary>
/// One JSON-RPC answer from signal-cli. <see cref="Code"/> is the JSON-RPC error code (0 when <see cref="Ok"/>);
/// a transport failure is reported as -3 (signal-cli's IO error code), so callers treat both as transient.
/// </summary>
public sealed record SignalRpcResult(bool Ok, JsonElement Result, int Code, string? Message, JsonElement Data, int Status)
{
    /// <summary>The daemon no longer holds the account (a silent <c>AUTHENTICATION_FAILED</c> removes it): permanent.</summary>
    public bool IsUnknownAccount
        => Code == -32602 && Message?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary><c>register</c> wants a captcha (a user error, -1, whose text names it).</summary>
    public bool IsCaptchaRequired
        => Code == -1 && Message?.Contains("captcha", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The server's rate limit (-5), whose <c>data</c> may say how long to wait.</summary>
    public bool IsRateLimited => Code == -5;

    public TimeSpan? RetryAfter
        => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("retryAfterSeconds", out var seconds)
            && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out var value) && value >= 0
            ? TimeSpan.FromSeconds(value)
            : null;
}

/// <summary>A send's outcome in the gateway's terms, plus the two facts the adapter reacts to beyond the kind.</summary>
/// <param name="ProofRequired">Signal asked for a captcha (a result <c>token</c>); the panel shows a fault reason.</param>
/// <param name="IsUnknownAccount">The daemon lost the account; the adapter turns this into a <see cref="ChatAuthException"/>.</param>
public sealed record SignalSendOutcome(ChatSendResult Result, bool ProofRequired = false, bool IsUnknownAccount = false);

/// <summary>
/// Hand-rolled JSON-RPC 2.0 over signal-cli's loopback HTTP daemon (ADR 0111, phase 3):
/// <c>POST api/v1/rpc</c> and the SSE stream <c>GET api/v1/events?account=</c>. The base URL comes from
/// <see cref="ISignalDaemon"/>, which only answers while this instance may run the daemon.
///
/// <para>Both named clients drop the inherited retry pipeline (CLAUDE.md rule 8: a retried <c>send</c> that timed out
/// after signal-cli accepted it sends the reply twice), and the events URL carries the account's phone number, so
/// request logging is removed and OpenTelemetry's HttpClient span is suppressed. No method here logs a number.</para>
/// </summary>
public sealed class SignalRpcClient(IHttpClientFactory httpClients, ISignalDaemon daemon)
{
    /// <summary>Named client for RPC calls.</summary>
    public const string RpcHttpClientName = "signal-rpc";

    /// <summary>Named client for the SSE stream: it never times out, the supervisor owns its lifetime.</summary>
    public const string EventsHttpClientName = "signal-events";

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static void AddHttpClients(IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(RpcHttpClientName, http => http.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler(() => new TelegramSpanSuppressingHandler())
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();
        services.AddHttpClient(EventsHttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(() => new TelegramSpanSuppressingHandler())
            .RemoveAllLoggers()
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    /// <summary>Whether <paramref name="chatId"/> addresses a person (an ACI, or an E.164 number) rather than a group.</summary>
    public static bool IsDirect(string chatId) => Guid.TryParse(chatId, out _) || chatId.StartsWith('+');

    public async Task<SignalRpcResult> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var baseUrl = await daemon.EnsureRunningAsync(cancellationToken);
        var client = httpClients.CreateClient(RpcHttpClientName);
        var body = new { jsonrpc = "2.0", method, @params = parameters, id = Guid.NewGuid().ToString("N") };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "api/v1/rpc"))
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            return await ReadResponseAsync(response, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Transport();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Transport();
        }
    }

    private static SignalRpcResult Transport() => new(false, default, -3, "transport failure", default, 0);

    private static async Task<SignalRpcResult> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        JsonElement json;
        try
        {
            json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (JsonException)
        {
            return new SignalRpcResult(false, default, status >= 500 ? -3 : -32603, "unreadable answer", default, status);
        }

        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            return ErrorResult(error, status);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SignalRpcResult(false, default, status >= 500 ? -3 : -32603, "http " + status, default, status);
        }

        var result = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("result", out var value) ? value.Clone() : default;
        return new SignalRpcResult(true, result, 0, null, default, status);
    }

    private static SignalRpcResult ErrorResult(JsonElement error, int status)
    {
        var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var number) ? number : -32603;
        var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        return new SignalRpcResult(false, default, code, message, error.TryGetProperty("data", out var data) ? data.Clone() : default, status);
    }

    /// <summary>Sends <paramref name="text"/> (with its <c>textStyle</c> entries and data-URI attachments) to a person or a group.</summary>
    public async Task<SignalSendOutcome> SendAsync(
        string account,
        string chatId,
        string text,
        IReadOnlyList<string>? styles,
        IReadOnlyList<string>? attachments,
        long? quoteTimestamp,
        string? quoteAuthor,
        CancellationToken cancellationToken)
    {
        var parameters = Target(account, chatId);
        parameters["message"] = text;
        if (styles is { Count: > 0 })
        {
            parameters["textStyle"] = styles;
        }

        if (attachments is { Count: > 0 })
        {
            parameters["attachments"] = attachments;
        }

        if (quoteTimestamp is not null && quoteAuthor is not null)
        {
            parameters["quoteTimestamp"] = quoteTimestamp;
            parameters["quoteAuthor"] = quoteAuthor;
        }

        return ToSendOutcome(await CallAsync("send", parameters, cancellationToken));
    }

    public async Task<SignalRpcResult> SendTypingAsync(string account, string chatId, bool stop, CancellationToken cancellationToken)
    {
        var parameters = Target(account, chatId);
        if (stop)
        {
            parameters["stop"] = true;
        }

        return await CallAsync("sendTyping", parameters, cancellationToken);
    }

    /// <summary>
    /// Reads a downloaded attachment (<c>getAttachment</c>, base64 <c>data</c>). <c>null</c> when it is gone, over
    /// <paramref name="maxBytes"/> (checked on the base64 length before decoding), or the call failed.
    /// </summary>
    public async Task<byte[]?> GetAttachmentAsync(string account, string attachmentId, string chatId, long maxBytes, CancellationToken cancellationToken)
    {
        var parameters = Target(account, chatId);
        parameters["id"] = attachmentId;
        var answer = await CallAsync("getAttachment", parameters, cancellationToken);
        if (!answer.Ok || answer.Result.ValueKind != JsonValueKind.Object
            || !answer.Result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var base64 = data.GetString()!;
        if (base64.Length / 4 * 3 - 2 > maxBytes)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length > maxBytes ? null : bytes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The accounts the daemon holds, or <c>null</c> when the call failed (not the same as "none").</summary>
    public async Task<IReadOnlyList<SignalAccount>?> ListAccountsAsync(CancellationToken cancellationToken)
    {
        var answer = await CallAsync("listAccounts", null, cancellationToken);
        if (!answer.Ok || answer.Result.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var accounts = new List<SignalAccount>();
        foreach (var item in answer.Result.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.String)
            {
                accounts.Add(new SignalAccount(number.GetString()!, item.TryGetProperty("aci", out var aci) && aci.ValueKind == JsonValueKind.String ? aci.GetString() : null));
            }
        }

        return accounts;
    }

    public Task<SignalRpcResult> RegisterAsync(string account, string? captcha, bool voice, CancellationToken cancellationToken)
        => CallAsync("register", Params(("account", account), ("captcha", captcha), ("voice", voice ? true : null)), cancellationToken);

    public Task<SignalRpcResult> VerifyAsync(string account, string code, string? pin, CancellationToken cancellationToken)
        => CallAsync("verify", Params(("account", account), ("verificationCode", code), ("pin", pin)), cancellationToken);

    public Task<SignalRpcResult> UnregisterAsync(string account, CancellationToken cancellationToken)
        => CallAsync("unregister", Params(("account", account)), cancellationToken);

    public Task<SignalRpcResult> DeleteLocalAccountDataAsync(string account, CancellationToken cancellationToken)
        => CallAsync("deleteLocalAccountData", Params(("account", account)), cancellationToken);

    /// <summary>
    /// Yields the data of each <c>receive</c> event on this account's SSE stream until it ends. A 400 means the daemon
    /// does not know the account (<see cref="ChatAuthException"/>); any other non-200 open throws.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> ReadEventsAsync(string account, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var baseUrl = await daemon.EnsureRunningAsync(cancellationToken);
        var client = httpClients.CreateClient(EventsHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "api/v1/events?account=" + Uri.EscapeDataString(account)));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ChatAuthException("Signal no longer has this number registered; register it again.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"The Signal event stream answered {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName == "receive" && TryParse(data.ToString(), out var evt))
                {
                    yield return evt;
                }

                eventName = null;
                data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart(' '));
            }
        }
    }

    private static bool TryParse(string json, out JsonElement element)
    {
        element = default;
        if (json.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Null values are left out: signal-cli reads an absent parameter as "not given", and a JSON null may not parse.
    private static Dictionary<string, object?> Params(params (string Name, object? Value)[] values)
        => values.Where(v => v.Value is not null).ToDictionary(v => v.Name, v => v.Value);

    private static Dictionary<string, object?> Target(string account, string chatId)
    {
        var parameters = new Dictionary<string, object?> { ["account"] = account };
        if (IsDirect(chatId))
        {
            parameters["recipient"] = new[] { chatId };
        }
        else
        {
            parameters["groupId"] = chatId;
        }

        return parameters;
    }

    // SC6. A group send is fine when anyone got it; otherwise the first failure names the kind.
    private static SignalSendOutcome ToSendOutcome(SignalRpcResult answer)
    {
        if (!answer.Ok)
        {
            return ErrorOutcome(answer);
        }

        var timestamp = answer.Result.ValueKind == JsonValueKind.Object && answer.Result.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
            ? ts.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var results = answer.Result.ValueKind == JsonValueKind.Object && answer.Result.TryGetProperty("results", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : [];
        if (results.Count == 0 || results.Any(r => Type(r) == "SUCCESS"))
        {
            return new SignalSendOutcome(ChatSendResult.Ok(timestamp));
        }

        if (results.Any(r => r.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String))
        {
            return new SignalSendOutcome(ChatSendResult.Fail(ChatSendErrorKind.RateLimited), ProofRequired: true);
        }

        var kind = results.Select(r => Type(r) switch
        {
            "UNREGISTERED_FAILURE" => ChatSendErrorKind.NotFound,
            "NETWORK_FAILURE" => ChatSendErrorKind.Transient,
            _ => ChatSendErrorKind.Unknown,
        }).Min();
        return new SignalSendOutcome(ChatSendResult.Fail(kind));
    }

    private static SignalSendOutcome ErrorOutcome(SignalRpcResult error)
    {
        if (error.IsUnknownAccount)
        {
            return new SignalSendOutcome(ChatSendResult.Fail(ChatSendErrorKind.Unknown), IsUnknownAccount: true);
        }

        var kind = error.Code switch
        {
            -5 => ChatSendErrorKind.RateLimited,
            -3 => ChatSendErrorKind.Transient,
            _ when error.Status >= 500 => ChatSendErrorKind.Transient,
            _ => ChatSendErrorKind.Unknown,
        };
        return new SignalSendOutcome(ChatSendResult.Fail(kind, kind == ChatSendErrorKind.RateLimited ? error.RetryAfter : null));
    }

    private static string? Type(JsonElement result)
        => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
}
