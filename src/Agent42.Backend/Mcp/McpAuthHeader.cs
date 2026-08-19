using Microsoft.Identity.Client;

namespace Agent42.Backend;

/// <summary>
/// Supplies the value for a single auth header on each outgoing MCP request (config-driven remote MCP
/// servers, Phase 2). A provider abstraction so a static value and a dynamically-refreshed token share one
/// <see cref="AuthHeaderHandler"/>.
/// </summary>
public interface IAuthHeaderProvider
{
    /// <summary>The header to stamp (e.g. <c>Authorization</c>).</summary>
    string HeaderName { get; }

    /// <summary>The header value for this request (a token provider may refresh here).</summary>
    ValueTask<string> GetHeaderValueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// OAuth2 <c>client_credentials</c> via MSAL — the non-interactive service-principal path (Entra ID →
/// Dataverse). MSAL caches and silently refreshes the token, so calling <c>AcquireTokenForClient</c> per
/// request is cheap (it returns the cached token until near expiry).
/// </summary>
public sealed class ClientCredentialsTokenProvider : IAuthHeaderProvider
{
    private readonly IConfidentialClientApplication _app;
    private readonly string[] _scopes;

    public string HeaderName => "Authorization";

    public ClientCredentialsTokenProvider(string authority, string clientId, string clientSecret, string[] scopes)
    {
        _app = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(authority)
            .Build();
        _scopes = scopes;
    }

    public async ValueTask<string> GetHeaderValueAsync(CancellationToken cancellationToken)
    {
        var result = await _app.AcquireTokenForClient(_scopes).ExecuteAsync(cancellationToken);
        return $"Bearer {result.AccessToken}";
    }
}

/// <summary>
/// Stamps the auth header on every outgoing MCP request from an <see cref="IAuthHeaderProvider"/>. Used only
/// for auth modes whose value is dynamic (ClientCredentials refreshes per request); the static Header mode
/// uses the transport's <c>AdditionalHeaders</c> instead and needs no handler.
/// </summary>
public sealed class AuthHeaderHandler(IAuthHeaderProvider provider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var value = await provider.GetHeaderValueAsync(cancellationToken);
        request.Headers.Remove(provider.HeaderName);
        request.Headers.TryAddWithoutValidation(provider.HeaderName, value);
        return await base.SendAsync(request, cancellationToken);
    }
}
