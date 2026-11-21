using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Layer two of the Auto router's option text (ADR 0137): a public description of a model, by name. Cache-only on the hot path
/// so a lookup can never delay a turn, and a miss simply falls through to the description derived from the capability flags.
/// </summary>
public interface IModelCardSource
{
    /// <summary>The cached description for <paramref name="modelName"/>, or null (unknown, or the listing is not loaded yet).</summary>
    string? TryGetDescription(string modelName);
}

/// <summary>
/// A <see cref="IModelCardSource"/> over OpenRouter's public models listing: a plain GET to ONE fixed URL (never a user-supplied
/// one) that needs no auth and carries nothing about the user or their conversation. The listing is ~650 KB, fetched once per
/// <see cref="Ttl"/> in the background; a failed fetch is retried after <see cref="FailureTtl"/>, never per turn.
///
/// <para>Matching is exact on a normalised name (lower case, <c>.</c> read as <c>-</c>, provider prefix and trailing date
/// dropped), because a user's deployment names are arbitrary: a near-miss is a miss, not a guess.</para>
/// </summary>
public sealed class OpenRouterModelCardSource(HttpClient http, TimeProvider time, ILogger<OpenRouterModelCardSource> logger) : IModelCardSource
{
    public const string HttpClientName = "model-cards";
    public const string ListingUrl = "https://openrouter.ai/api/v1/models";

    private const int MaxDescriptionChars = 280;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string> _cards = new Dictionary<string, string>();
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;
    private Task? _loading;

    public static void AddHttpClient(IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    public string? TryGetDescription(string modelName)
    {
        EnsureLoading();
        IReadOnlyDictionary<string, string> cards;
        lock (_gate)
        {
            cards = _cards;
        }

        return cards.GetValueOrDefault(Normalize(modelName));
    }

    // Starts a background refresh when the cache is stale and none is running; the caller never waits for it.
    private void EnsureLoading()
    {
        lock (_gate)
        {
            if (_loading is not null || time.GetUtcNow() < _validUntil)
            {
                return;
            }

            _loading = Task.Run(LoadAsync);
        }
    }

    /// <summary>Fetches the listing now. The hot path never awaits it (it starts it in the background); a test or a warm-up can.</summary>
    public Task LoadAsync() => LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        try
        {
            var json = await http.GetStringAsync(ListingUrl);
            var cards = Parse(json);
            lock (_gate)
            {
                _cards = cards;
                _validUntil = time.GetUtcNow() + Ttl;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not load the public model listing; Auto falls back to capability-based descriptions");
            lock (_gate)
            {
                _validUntil = time.GetUtcNow() + FailureTtl;
            }
        }
        finally
        {
            lock (_gate)
            {
                _loading = null;
            }
        }
    }

    /// <summary>Reads <c>data[].{id,description,context_length}</c> into normalised-name → short description.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string json)
    {
        var cards = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return cards;
        }

        foreach (var model in data.EnumerateArray())
        {
            // ":batch", ":free" and friends are variants of the same model; the plain entry is the card.
            var id = model.TryGetProperty("id", out var i) ? i.GetString() : null;
            var description = model.TryGetProperty("description", out var d) ? d.GetString() : null;
            if (id is null || id.Contains(':') || string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            var context = model.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
            cards.TryAdd(Normalize(id), Shorten(description, context));
        }

        return cards;
    }

    /// <summary>Lower case, provider prefix dropped, <c>.</c> read as <c>-</c>, and a trailing date (<c>-2024-08-06</c>, <c>-20240806</c>) removed.</summary>
    public static string Normalize(string modelName)
    {
        var name = modelName.Trim().ToLowerInvariant();
        name = name[(name.LastIndexOf('/') + 1)..].Replace('.', '-');
        name = System.Text.RegularExpressions.Regex.Replace(name, @"-(\d{4}-\d{2}-\d{2}|\d{8})$", "");
        return name;
    }

    private static string Shorten(string description, long contextLength)
    {
        var text = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaxDescriptionChars)
        {
            text = text[..MaxDescriptionChars].TrimEnd() + "…";
        }

        return contextLength > 0 ? $"{text} Context window {contextLength / 1000}k tokens." : text;
    }
}
