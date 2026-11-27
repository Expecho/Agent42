namespace Agent42.Backend;

/// <summary>Shared wiring for the named <see cref="HttpClient"/>s of the chat platform adapters (ADR 0111).</summary>
public static class ChatPlatformHttpClient
{
    /// <summary>
    /// Drops the retry pipeline <c>AddServiceDefaults</c> adds to every client (CLAUDE.md rule 8): a retried send that
    /// timed out after the platform accepted it posts the reply twice. Every chat client ends its registration with this;
    /// <c>ProjectLayoutTests.EveryNamedHttpClient_RemovesTheInheritedRetryPipeline</c> accepts it as the opt-out.
    /// </summary>
    public static IHttpClientBuilder WithoutInheritedRetry(this IHttpClientBuilder builder)
    {
#pragma warning disable EXTEXP0001
        return builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }
}
