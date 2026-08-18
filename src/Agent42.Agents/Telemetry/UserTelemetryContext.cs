namespace Agent42.Agents;

/// <summary>
/// A serializable snapshot of the signed-in user's identity, extracted once from the Easy Auth
/// request headers and propagated to downstream spans via Orleans <c>RequestContext</c> (ADR 0010).
/// It crosses the grain-call boundary — the production chat-completion span is created *inside*
/// <see cref="AgentGrain"/> — so it must be Orleans-serializable; hence <c>[GenerateSerializer]</c>
/// with explicit member ids (all fields are <c>string</c>/<c>string[]</c>, natively serializable).
///
/// It owns the OpenTelemetry <c>user.*</c> attribute-key constants and the single
/// <c>RequestContext</c> key, so the middleware (writer), the span processor (reader), and the
/// header extractor all agree on one shape. <see cref="ToTags"/> yields the exact
/// <c>KeyValuePair&lt;string, object&gt;</c> tag form the processor stamps via <c>SetTag</c>
/// (roles as a <c>string[]</c> multi-value attribute).
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UserTelemetryContext")]
public sealed record UserTelemetryContext(
    [property: Id(0)] string Id,
    [property: Id(1)] string? Name,
    [property: Id(2)] string? Email,
    [property: Id(3)] string? FullName,
    [property: Id(4)] string[]? Roles,
    [property: Id(5)] string? Idp)
{
    // OpenTelemetry semantic-convention user.* attribute keys. user.idp is a deliberate
    // deviation — there is no semconv key for the identity provider (ADR 0009).
    public const string UserIdTag = "user.id";
    public const string UserNameTag = "user.name";
    public const string UserEmailTag = "user.email";
    public const string UserFullNameTag = "user.full_name";
    public const string UserRolesTag = "user.roles";
    public const string UserIdpTag = "user.idp";

    /// <summary>
    /// The Orleans <c>RequestContext</c> key under which the request's identity is propagated to
    /// downstream/grain spans (ADR 0010). <c>RequestContext</c> is an in-cluster channel (Orleans'
    /// own protocol), never an outbound HTTP header, so identity reaches the grain-side chat span
    /// without shipping PII to Azure OpenAI in a <c>baggage</c> header — the load-bearing reason the
    /// design uses <c>RequestContext</c> and not <c>Activity.Baggage</c>.
    /// </summary>
    public const string RequestContextKey = "agent42.user-telemetry";

    /// <summary>
    /// The <c>user.*</c> attributes for this identity, in the tag shape a span processor / enrichment
    /// callback stamps via <c>SetTag</c>. <c>user.id</c> is always emitted (it carries the local-dev
    /// fallback when no Easy Auth id is present); every other attribute is emitted only when present.
    /// <c>user.roles</c> is a <c>string[]</c> so OTLP exports it as a multi-value attribute.
    /// </summary>
    public IEnumerable<KeyValuePair<string, object>> ToTags()
    {
        yield return new(UserIdTag, Id);

        if (!string.IsNullOrWhiteSpace(Name))
        {
            yield return new(UserNameTag, Name);
        }

        if (!string.IsNullOrWhiteSpace(Idp))
        {
            yield return new(UserIdpTag, Idp);
        }

        if (!string.IsNullOrWhiteSpace(Email))
        {
            yield return new(UserEmailTag, Email);
        }

        if (!string.IsNullOrWhiteSpace(FullName))
        {
            yield return new(UserFullNameTag, FullName);
        }

        if (Roles is { Length: > 0 })
        {
            yield return new(UserRolesTag, Roles);
        }
    }
}
