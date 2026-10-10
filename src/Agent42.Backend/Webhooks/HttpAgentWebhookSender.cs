using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Signs and POSTs one outbound webhook event (ADR 0094) over the named client <see cref="HttpClientName"/>, which
/// <see cref="WebhookRegistration"/> builds on the connect-time private-address guard with redirects off and the
/// AddServiceDefaults resilience pipeline removed. The retry is this class's own bounded loop instead.
///
/// <para><b>Never throws</b> (the <see cref="IPushNotificationSender"/> contract): every outcome comes back as a
/// <see cref="WebhookDeliveryResult"/> carrying a status code and an error CLASS — never a response body.</para>
/// </summary>
public sealed class HttpAgentWebhookSender : IAgentWebhookSender
{
    /// <summary>The named HttpClient this sender uses.</summary>
    public const string HttpClientName = "agent-webhooks";

    /// <summary>How many times one delivery is attempted.</summary>
    public const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebhookSecretProtector _protector;
    private readonly ILogger<HttpAgentWebhookSender> _logger;

    public HttpAgentWebhookSender(
        IHttpClientFactory httpClientFactory, IWebhookSecretProtector protector, ILogger<HttpAgentWebhookSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>The waits between attempts: 2 s, then 8 s. Settable so a test does not sleep through them.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];

    public async Task<WebhookDeliveryResult> SendAsync(
        AgentWebhookTarget target, string deliveryId, AgentWebhookEvent evt, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (_protector.Unprotect(target.SecretCiphertext) is not { Length: > 0 } secret)
        {
            return new WebhookDeliveryResult(null, WebhookLimits.SecretUnavailableError, 0, TimeSpan.Zero);
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = Body(deliveryId, evt);
        var signature = WebhookSigner.Sign(secret, deliveryId, timestamp, body);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var outcome = (Status: (int?)null, Error: (string?)null, Final: false);
        var attempt = 0;
        while (attempt < MaxAttempts)
        {
            attempt++;
            outcome = await AttemptAsync(client, target.Url, deliveryId, timestamp, signature, body, cancellationToken);
            if (outcome.Final || attempt == MaxAttempts)
            {
                break;
            }

            try
            {
                await Task.Delay(RetryDelays[Math.Min(attempt - 1, RetryDelays.Count - 1)], cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new WebhookDeliveryResult(outcome.Status, outcome.Error, attempt, Stopwatch.GetElapsedTime(started));
    }

    // One POST. Final means "do not retry": a 2xx, a 4xx other than 429 (including a 3xx, since redirects are not
    // followed), a refused private address, or a cancellation by the caller.
    private async Task<(int? Status, string? Error, bool Final)> AttemptAsync(
        HttpClient client, string url, string deliveryId, long timestamp, string signature, string body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("webhook-id", deliveryId);
            request.Headers.Add("webhook-timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("webhook-signature", signature);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            var retryable = status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests;
            return (status, null, !retryable);
        }
        catch (HttpRequestException ex) when (IsBlockedAddress(ex))
        {
            _logger.LogWarning("Webhook delivery {DeliveryId} refused: the URL resolves only to non-public addresses", deliveryId);
            return (null, "blocked-address", true);
        }
        catch (HttpRequestException)
        {
            return (null, "network", false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "timeout", false);
        }
        catch (OperationCanceledException)
        {
            return (null, "cancelled", true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook delivery {DeliveryId} failed unexpectedly", deliveryId);
            return (null, "error", true);
        }
    }

    private static bool IsBlockedAddress(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is BlockedAddressException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The JSON body (ADR 0094 decision 9). A test event carries the agent and nothing else; a turn event carries the
    /// turn. Every field is always present, so a receiver's schema never has to guess at a missing key.
    /// </summary>
    public static string Body(string deliveryId, AgentWebhookEvent evt)
    {
        var agent = new { id = evt.AgentId, name = evt.AgentName };
        var timestamp = evt.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        object payload = evt.Type == AgentWebhookEvent.Test
            ? new { type = evt.Type, id = deliveryId, timestamp, agent }
            : new
            {
                type = evt.Type,
                id = deliveryId,
                timestamp,
                agent,
                conversationId = evt.ConversationId,
                trigger = evt.Trigger,
                reminderName = evt.ReminderName,
                prompt = evt.Prompt,
                promptTruncated = evt.PromptTruncated,
                reply = evt.Reply,
                replyTruncated = evt.ReplyTruncated,
                stopped = evt.Stopped,
                imageCount = evt.ImageCount,
                replacesLastTurn = evt.ReplacesLastTurn,
            };
        return JsonSerializer.Serialize(payload, BodyJson);
    }
}
