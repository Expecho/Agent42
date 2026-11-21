using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Calls a Microsoft Decision-1 deployment on the user's Foundry resource (ADR 0137):
/// <c>POST &lt;resource&gt;/providers/microsoft/v1/systemone</c> with an <c>api-key</c> header and one <c>choice</c> question.
/// Request and response shapes come from a real capture (the plan's Step 1 spike, 2026-11-21), not from the docs.
///
/// <para>Failure never fails the turn: any non-2xx, timeout or bad JSON returns <c>null</c> and the caller keeps its model.
/// The endpoint comes from the user's stored provider, never a request (ADR 0018: not SSRF-guarded). The named client opts out of
/// <c>AddServiceDefaults</c>' retry pipeline (CLAUDE.md rule 8): a retry would double the delay of a turn that waits on it.</para>
/// </summary>
public sealed class FoundryDecisionClient(
    HttpClient http, DecisionProviderSettings settings, ILogger<FoundryDecisionClient> logger) : IDecisionClient
{
    public const string HttpClientName = "decision-router";

    private const string QuestionKey = "route";
    private const string Path = "/providers/microsoft/v1/systemone";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static void AddHttpClient(IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName, http => http.Timeout = Timeout + TimeSpan.FromSeconds(1))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    public async Task<IReadOnlyDictionary<string, double>?> ChooseAsync(
        string state, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken = default)
    {
        if (!TryBuildUri(settings.Endpoint, out var uri))
        {
            logger.LogWarning("Decision router endpoint is not a valid absolute URL; keeping the current model");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(BuildBody(settings.Deployment, state, options)) };
            request.Headers.Add("api-key", settings.ApiKey);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Decision router answered {Status}; keeping the current model", (int)response.StatusCode);
                return null;
            }

            return ParseProbabilities(await response.Content.ReadAsStringAsync(timeout.Token), logger);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            // A caller's own cancellation is the caller's to handle; a timeout or transport error is a router failure.
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning(ex, "Decision router call failed; keeping the current model");
            return null;
        }
    }

    /// <summary>The endpoint is the resource ROOT. A project URL (<c>/api/projects/p</c>) is cut back to its origin, because
    /// the project-scoped route needs an api-version the root route does not (spike, 2026-11-21).</summary>
    public static bool TryBuildUri(string endpoint, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = new Uri(parsed.GetLeftPart(UriPartial.Authority) + Path);
        return true;
    }

    public static object BuildBody(string deployment, string state, IReadOnlyDictionary<string, string> options) => new
    {
        model = deployment,
        state,
        questions = new Dictionary<string, object>
        {
            [QuestionKey] = new
            {
                type = "choice",
                instructions = "Pick the assistant model best suited to answer the user's message. "
                    + "Choose the cheapest model that can do the job well; pick the cannot-tell option when unsure.",
                criteria = options,
            },
        },
    };

    /// <summary>Reads <c>answers.route.probabilities</c>; null when the shape is not what the spike captured.</summary>
    public static IReadOnlyDictionary<string, double>? ParseProbabilities(string json, ILogger? logger = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            logger?.LogWarning("Decision router answered with invalid JSON; keeping the current model");
            return null;
        }

        if (root?["answers"]?[QuestionKey]?["probabilities"] is not JsonObject probabilities)
        {
            logger?.LogWarning("Decision router answered without probabilities; keeping the current model");
            return null;
        }

        var result = new Dictionary<string, double>();
        foreach (var (key, value) in probabilities)
        {
            if (value is JsonValue v && v.TryGetValue<double>(out var p))
            {
                result[key] = p;
            }
        }

        // Router tokens are outside the usage dashboard (ADR 0137): record them on the current span instead.
        if (root["usage"]?["input_tokens"] is JsonValue tokens && tokens.TryGetValue<long>(out var input))
        {
            Activity.Current?.SetTag("agent42.router.input_tokens", input);
        }

        return result.Count == 0 ? null : result;
    }
}
