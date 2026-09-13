namespace Agent42.Backend;

/// <summary>
/// Maps an additional Entra object id onto the object id that actually owns the data (ADR 0074), so
/// one person holding two Entra accounts reaches one Agent42 dataset without moving a byte.
///
/// <para><b>An alias entry is an authorization grant, not a preference.</b> The userId returned by
/// <c>CurrentUser.GetUserId</c> is the isolation boundary for every durable store in this app — Cosmos
/// grain keys, <c>doc-chunks</c> and <c>chat-messages</c> partition keys, blob prefixes, the Hindsight
/// memory bank id. ADR 0001's "one user can never address another's conversation" still holds of
/// <em>userIds</em>; what this changes is that two Entra principals can map to one. A wrong entry hands
/// one principal another's entire dataset, which is why the validation below drops aggressively and why
/// <c>UserIdAliasTests</c> cross-checks the shipped map against the Easy Auth allowlist in Bicep.</para>
///
/// <para>Deliberately free of DI and of <c>HttpContext</c>: the rule is the security surface, so it is
/// unit-testable directly rather than only through a host — the <see cref="DashboardOwnerGate"/> shape.</para>
/// </summary>
public sealed class UserIdAliases
{
    /// <summary>Configuration section holding <c>{alias object id}: {canonical object id}</c> pairs.</summary>
    public const string SectionName = "Identity:Aliases";

    /// <summary>
    /// The identity <c>CurrentUser.ResolveUserId</c> falls back to when a request carries no Easy Auth
    /// principal header. Forbidden on <b>both</b> sides of an alias: as a value it would attach a real
    /// account to the unauthenticated fallback's data, and as a key it would alias every
    /// header-less request. Belt and braces — <c>ResolveUserId</c> also never consults this map on the
    /// no-header branch — but the <c>Dashboard:OwnerOid</c> <c>//DANGER</c> invariant is worth pinning twice.
    /// </summary>
    public const string OfflineFallbackUserId = CurrentUser.LocalDevelopmentUserId;

    /// <summary>No aliases configured: <see cref="Resolve"/> is then a pure passthrough.</summary>
    public static readonly UserIdAliases Empty = new(new(StringComparer.OrdinalIgnoreCase), []);

    private readonly Dictionary<string, string> _aliases;

    private UserIdAliases(Dictionary<string, string> aliases, IReadOnlyList<string> droppedEntries)
    {
        _aliases = aliases;
        DroppedEntries = droppedEntries;
    }

    /// <summary>
    /// One human-readable line per entry the validation refused, for the startup log and the
    /// Configuration health tab (ADR 0029). Empty is the healthy state.
    /// </summary>
    public IReadOnlyList<string> DroppedEntries { get; }

    /// <summary>How many aliases survived validation.</summary>
    public int Count => _aliases.Count;

    /// <summary>Whether no alias is in effect, so resolution is a passthrough.</summary>
    public bool IsEmpty => _aliases.Count == 0;

    /// <summary>
    /// Binds and validates the map. <b>Never throws</b>: a malformed alias entry degrades to "no alias"
    /// and is reported through <see cref="DroppedEntries"/>, rather than taking the app down. Eager-throw
    /// in this repo is reserved for the availability path (<c>WebSearch:Endpoint</c>), and identity
    /// aliasing is not on it — an app that boots with the alias inert is recoverable, one that refuses to
    /// boot is not.
    /// </summary>
    public static UserIdAliases FromConfiguration(IConfiguration configuration)
    {
        var candidates = new List<KeyValuePair<string, string>>();
        var dropped = new List<string>();

        foreach (var entry in configuration.GetSection(SectionName).GetChildren())
        {
            // This repo documents appsettings.json with "//"-prefixed sibling keys. One written inside
            // this section is prose, not a mis-typed alias, so it is skipped SILENTLY rather than
            // reported as dropped — otherwise documenting the section would raise a health warning.
            if (entry.Key.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var value = entry.Value ?? string.Empty;
            if (Reject(entry.Key, value) is { } reason)
            {
                dropped.Add($"'{entry.Key}' → '{value}': {reason}");
                continue;
            }

            candidates.Add(new(entry.Key.Trim(), value.Trim()));
        }

        return Build(candidates, dropped);
    }

    /// <summary>
    /// The canonical id for <paramref name="rawUserId"/>, or <paramref name="rawUserId"/> itself when it
    /// is not aliased. <b>Single hop by construction</b> — one dictionary lookup, never a walk — which is
    /// what makes a chain unresolvable rather than partially resolved, and is why
    /// <see cref="Build"/> refuses chains outright instead of leaving them to be followed here.
    /// </summary>
    public string Resolve(string rawUserId)
        => _aliases.TryGetValue(rawUserId, out var canonical) ? canonical : rawUserId;

    /// <summary>
    /// Per-entry validation. Returns the reason an entry is unusable, or <c>null</c> when it is fine.
    /// </summary>
    private static string? Reject(string key, string value)
    {
        var alias = key.Trim();
        var canonical = value.Trim();

        if (alias.Length == 0 || canonical.Length == 0)
        {
            return "blank alias or canonical id";
        }

        if (Is(alias, OfflineFallbackUserId) || Is(canonical, OfflineFallbackUserId))
        {
            return $"names the unauthenticated fallback identity '{OfflineFallbackUserId}'";
        }

        if (Is(alias, canonical))
        {
            return "aliases an id to itself, which is inert";
        }

        // The canonical id is concatenated into the grain key "{userId}/{conversationId}" (AgentApi.AgentKey)
        // which AgentGrain splits on the FIRST '/', and it is also a blob-path prefix. Requiring the exact
        // hyphenated GUID form Entra actually emits makes a re-partitioned key structurally impossible
        // rather than merely checked for — a stricter and shorter guard than hunting separators.
        return Guid.TryParseExact(canonical, "D", out _)
            ? null
            : "canonical id is not a bare hyphenated GUID, which an Entra object id always is";
    }

    /// <summary>
    /// Drops chains and cycles, then materialises the map with an explicit ignore-case comparer.
    ///
    /// <para><b>A chained entry is dropped whole, on both sides.</b> An id appearing as both an alias and
    /// a canonical id (A→B, B→C, or the cycle A→B, B→A) means the author expected transitivity that
    /// <see cref="Resolve"/> deliberately does not provide. Dropping only the head would leave the tail
    /// live — a half-applied authorization grant, the worst of the three outcomes — so every entry
    /// touching such an id goes.</para>
    ///
    /// <para><b>Fan-in survives, deliberately.</b> Two aliases pointing at one canonical id (A→C, B→C) is
    /// a legitimate three-accounts-one-dataset configuration, and it is not a chain: C is a canonical id
    /// only, never an alias, so the intersection below is empty and both entries stand.</para>
    ///
    /// <para>The intersection is <see cref="StringComparer.OrdinalIgnoreCase"/> for the reason
    /// <see cref="DashboardOwnerGate.IsOwner"/> documents — a GUID in different casing is the same
    /// principal, so an ordinal check here would let <c>{A→b, B→C}</c> slip through as unchained while
    /// <see cref="Resolve"/> matched it case-insensitively.</para>
    /// </summary>
    private static UserIdAliases Build(
        List<KeyValuePair<string, string>> candidates, List<string> dropped)
    {
        var chained = candidates
            .Select(entry => entry.Key)
            .Intersect(candidates.Select(entry => entry.Value), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in candidates)
        {
            if (chained.Contains(alias) || chained.Contains(canonical))
            {
                dropped.Add(
                    $"'{alias}' → '{canonical}': part of an alias chain or cycle; aliases resolve in a "
                        + "single hop, so the whole chain is refused rather than partially applied");
                continue;
            }

            aliases[alias] = canonical;
        }

        return new UserIdAliases(aliases, dropped);
    }

    private static bool Is(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
