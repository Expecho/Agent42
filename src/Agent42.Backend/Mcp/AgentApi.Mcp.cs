using Agent42.Agents;
using Microsoft.AspNetCore.WebUtilities;

namespace Agent42.Backend;

/// <summary>
/// Remote MCP server CRUD (ADR 0024) and the interactive OAuth authorization-code + PKCE flow (ADR 0025). Both OAuth routes stay behind Easy Auth and are never on excludedPaths.
/// </summary>
public static partial class AgentApi
{
    private static void MapMcpEndpoints(IEndpointRouteBuilder app)
    {
        // The signed-in user's OWN configured MCP servers. Secrets are MASKED to hasHeaderValue flags: the
        // ciphertext never leaves the server, and the plaintext never existed here (the MCP analogue of
        // GET /api/providers, ADR 0018).
        app.MapGet("/api/mcp-servers", async (IGrainFactory grains, HttpContext http) =>
        {
            var overlay = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId()).GetAsync();
            return Results.Json(new McpServersResponse([.. overlay.Servers.Select(Mask)]));
        });

        // Creates an MCP server. The id is server-minted (unlike a provider key, a server name is not an
        // identifier — a user may add two servers with the same name), so POST always inserts. The API
        // ENCRYPTS the secret here — the grain only ever stores ciphertext.
        app.MapPost("/api/mcp-servers", async (McpServerUpsertRequest request, IMcpSecretProtector protector, IGrainFactory grains, HttpContext http) =>
        {
            var error = ValidateMcpServer(request);
            if (error is not null)
            {
                return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var id = Guid.NewGuid().ToString("n");
            var stored = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId())
                .SaveAsync(ToStored(request, id, protector));
            return Results.Json(Mask(stored));
        });

        // Updates the server named by the route id. A BLANK secret means "keep the stored secret" (the
        // field is write-only in the UI) — ToStored maps blank to a null ciphertext, which the grain reads
        // as "keep".
        app.MapPut("/api/mcp-servers/{id}", async (string id, McpServerUpsertRequest request, IMcpSecretProtector protector, IGrainFactory grains, HttpContext http) =>
        {
            var error = ValidateMcpServer(request);
            if (error is not null)
            {
                return Results.Text(error, "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var stored = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId())
                .SaveAsync(ToStored(request, id, protector));
            return Results.Json(Mask(stored));
        });

        // Deletes a server from the user's overlay. A warm conversation grain picks up the change on its
        // next activation (the tool provider is version-cached), never mid-turn.
        app.MapDelete("/api/mcp-servers/{id}", async (string id, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId()).DeleteAsync(id);
            return Results.NoContent();
        });

        app.MapGet("/api/mcp/oauth/start", StartMcpOAuthAsync);

        app.MapGet("/api/mcp/oauth/callback", CompleteMcpOAuthAsync);
    }

    // An MCP endpoint comes from the authenticated user's OWN persisted config, never a model or a live
    // request — so, exactly like a user provider endpoint (ADR 0018), it is deliberately NOT SSRF
    // address-guarded and may be localhost (a self-hosted MCP server is a valid target). Validation is only structural:
    // absolute http/https, a parseable auth mode, and the fields that mode needs. None/Header (Phase 1) and
    // ClientCredentials (Phase 2) are supported; InteractiveOAuth is staged (Phase 3) and rejected here for
    // now. Returns an error message, or null when valid.
    private static string? ValidateMcpServer(McpServerUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "MCP server name must be non-empty.";
        }

        if (string.IsNullOrWhiteSpace(request.Endpoint)
            || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Endpoint must be an absolute http or https URL.";
        }

        if (!Enum.TryParse<McpAuthMode>(request.Auth, ignoreCase: true, out var auth))
        {
            return $"Unknown auth mode '{request.Auth}'. Use 'None', 'Header', 'ClientCredentials', or 'InteractiveOAuth'.";
        }

        return auth switch
        {
            McpAuthMode.None => null,
            McpAuthMode.Header when string.IsNullOrWhiteSpace(request.HeaderName)
                => "Header auth requires a header name.",
            McpAuthMode.Header => null,
            // InteractiveOAuth: nothing extra is required at save time — the endpoints are discovered (PRM),
            // the client id is optional (dynamic registration otherwise), and tokens are obtained later via
            // the Connect flow (/api/mcp/oauth/start). Validation is only structural (endpoint above).
            McpAuthMode.InteractiveOAuth => null,
            // ClientCredentials (MSAL): the authority is an Entra login endpoint (absolute https); clientId
            // and ≥1 scope are required. The client secret is write-only (blank = keep on edit), so it is not
            // required at validation time — a missing secret degrades to a skipped server (best-effort).
            McpAuthMode.ClientCredentials when string.IsNullOrWhiteSpace(request.Authority)
                || !Uri.TryCreate(request.Authority, UriKind.Absolute, out var authUri)
                || authUri.Scheme != Uri.UriSchemeHttps
                => "ClientCredentials auth requires an absolute https authority (e.g. https://login.microsoftonline.com/{tenant}).",
            McpAuthMode.ClientCredentials when string.IsNullOrWhiteSpace(request.ClientId)
                => "ClientCredentials auth requires a client id.",
            McpAuthMode.ClientCredentials when request.Scopes is not { Count: > 0 }
                => "ClientCredentials auth requires at least one scope.",
            McpAuthMode.ClientCredentials => null,
            _ => $"Auth mode '{auth}' is not yet supported.",
        };
    }

    // Maps the request into the stored shape, encrypting each secret. A blank secret becomes a null
    // ciphertext: on create that is "no secret", on update the grain reads it as "keep the stored secret".
    // The auth mode is normalized to its enum name so the tool provider parses it back.
    //
    // This deliberately builds a FRESH record and so carries no KnownToolNames — there is no such field on the
    // request and the user cannot type one (they are DISCOVERED on connect, never configured). The names
    // survive an edit because UserMcpConfigGrain.SaveAsync keeps them, exactly as it keeps the ciphertexts.
    // Do not "fix" this by threading names through the request; do not remove that Keep clause either, or every
    // rename silently wipes the per-tool checkboxes.
    private static StoredMcpServer ToStored(McpServerUpsertRequest request, string id, IMcpSecretProtector protector) =>
        new(
            Id: id,
            Name: request.Name.Trim(),
            Endpoint: request.Endpoint.Trim(),
            Auth: Enum.Parse<McpAuthMode>(request.Auth, ignoreCase: true).ToString(),
            Enabled: request.Enabled,
            HeaderName: string.IsNullOrWhiteSpace(request.HeaderName) ? null : request.HeaderName.Trim(),
            HeaderValueCiphertext: string.IsNullOrWhiteSpace(request.HeaderValue) ? null : protector.Protect(request.HeaderValue),
            Authority: string.IsNullOrWhiteSpace(request.Authority) ? null : request.Authority.Trim(),
            ClientId: string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim(),
            ClientSecretCiphertext: string.IsNullOrWhiteSpace(request.ClientSecret) ? null : protector.Protect(request.ClientSecret),
            Scopes: request.Scopes is { Count: > 0 } ? [.. request.Scopes.Select(s => s.Trim()).Where(s => s.Length > 0)] : null);

    // Never returns a secret — only whether one is stored. Authority/clientId/scopes are non-secret and are
    // returned so the edit form can prefill them; the ciphertexts stay server-side.
    private static McpServerResponse Mask(StoredMcpServer s) =>
        new(
            s.Id, s.Name, s.Endpoint, s.Auth, s.Enabled, s.HeaderName,
            HasHeaderValue: !string.IsNullOrEmpty(s.HeaderValueCiphertext),
            Authority: s.Authority,
            ClientId: s.ClientId,
            Scopes: s.Scopes,
            HasClientSecret: !string.IsNullOrEmpty(s.ClientSecretCiphertext),
            HasToken: !string.IsNullOrEmpty(s.TokenCiphertext));

    // ————— interactive OAuth for MCP servers (Phase 3) —————
    //
    // BOTH endpoints stay BEHIND Easy Auth (ADR 0002) — NEVER in excludedPaths — or GetUserId() would
    // return the local-dev principal and the flow would bind tokens to the wrong (or no) user. The app
    // drives auth-code + PKCE itself because the SDK's redirect delegate is BLOCKING and can't bridge a
    // stateless two-request web flow (Step 0). The starting point is the user's OWN configured MCP endpoint
    // (never a model/request); the authorization/token URLs are then DISCOVERED from that server's responses
    // (response-controlled — accepted under the same "user's own server" rationale as ADR 0018; see the
    // McpOAuthClient note), and an upstream token-exchange error is NEVER reflected into the redirect.
    // Begins the flow: discover the server's authorization server, build the PKCE authorization URL, and
    // 302-redirect the browser to it. State + verifier are persisted per-user for the callback.
    private static async Task<IResult> StartMcpOAuthAsync(
        string serverId, McpOAuthClient oauth, IGrainFactory grains, HttpContext http)
    {
        var userId = http.GetUserId();
        var overlay = await grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
        var server = overlay.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
        if (server is null
            || !Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode)
            || mode != McpAuthMode.InteractiveOAuth)
        {
            return Results.Text("No InteractiveOAuth MCP server with that id.", "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        var mcpEndpoint = new Uri(server.Endpoint);
        var redirectUri = new Uri($"{http.Request.Scheme}://{http.Request.Host}/api/mcp/oauth/callback");
        try
        {
            var config = await oauth.DiscoverAsync(mcpEndpoint, http.RequestAborted);

            // A pre-registered client id is preferred; otherwise register dynamically if the server allows it.
            var clientId = server.ClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                if (config.RegistrationEndpoint is null)
                {
                    return Results.Text(
                        "This server needs a client id and does not support dynamic client registration. Set a client id on the server.",
                        "text/plain", statusCode: StatusCodes.Status400BadRequest);
                }

                clientId = await oauth.RegisterClientAsync(config.RegistrationEndpoint, redirectUri, http.RequestAborted);
                // Persist the registered client id so a later Connect/Reconnect reuses it instead of
                // minting (and orphaning) a fresh registration each time.
                await grains.GetGrain<IUserMcpConfigGrain>(userId).SaveAsync(server with { ClientId = clientId });
            }

            var scopes = server.Scopes is { Count: > 0 } ? server.Scopes : config.ScopesSupported;
            var verifier = McpPkce.NewVerifier();
            var state = McpPkce.NewState();

            await grains.GetGrain<IUserMcpOAuthPendingGrain>(userId).PutAsync(new McpOAuthPending(
                state, server.Id, verifier, clientId, config.TokenEndpoint.AbsoluteUri, redirectUri.AbsoluteUri, [.. scopes], mcpEndpoint.AbsoluteUri));

            var parameters = new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["state"] = state,
                ["code_challenge"] = McpPkce.Challenge(verifier),
                ["code_challenge_method"] = "S256",
                ["resource"] = mcpEndpoint.AbsoluteUri, // RFC 8707 resource indicator
            };
            if (scopes.Count > 0)
            {
                parameters["scope"] = string.Join(' ', scopes);
            }

            var authorizeUrl = QueryHelpers.AddQueryString(config.AuthorizationEndpoint.AbsoluteUri, parameters);
            return Results.Redirect(authorizeUrl);
        }
        catch (Exception ex)
        {
            return Results.Text($"Could not start authorization: {ex.Message}", "text/plain", statusCode: StatusCodes.Status502BadGateway);
        }

    }

    // Completes the flow: validate state, exchange the code for tokens, persist them ENCRYPTED on the
    // server, and redirect back to the app. A replayed callback finds no pending entry (one-time TakeAsync).
    private static async Task<IResult> CompleteMcpOAuthAsync(
        McpOAuthClient oauth, IMcpSecretProtector protector, IGrainFactory grains, ILoggerFactory loggerFactory, HttpContext http)
    {
        var userId = http.GetUserId();
        var query = http.Request.Query;
        if (query.TryGetValue("error", out var error))
        {
            return Results.Redirect($"/?mcp_oauth=error&reason={Uri.EscapeDataString(error.ToString())}");
        }

        var code = query["code"].ToString();
        var state = query["state"].ToString();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Results.Redirect("/?mcp_oauth=error&reason=missing_code_or_state");
        }

        var pending = await grains.GetGrain<IUserMcpOAuthPendingGrain>(userId).TakeAsync(state);
        if (pending is null)
        {
            return Results.Redirect("/?mcp_oauth=error&reason=unknown_state");
        }

        try
        {
            var tokens = await oauth.ExchangeCodeAsync(
                new Uri(pending.TokenEndpoint), pending.ClientId, code, pending.CodeVerifier,
                new Uri(pending.RedirectUri), new Uri(pending.Resource), pending.Scopes, http.RequestAborted);

            var configGrain = grains.GetGrain<IUserMcpConfigGrain>(userId);
            var server = (await configGrain.GetAsync()).Servers
                .FirstOrDefault(s => string.Equals(s.Id, pending.ServerId, StringComparison.OrdinalIgnoreCase));
            if (server is null)
            {
                return Results.Redirect("/?mcp_oauth=error&reason=server_deleted");
            }

            await configGrain.SaveAsync(server with { TokenCiphertext = protector.Protect(tokens.Serialize()) });
            return Results.Redirect("/?mcp_oauth=connected");
        }
        catch (Exception ex)
        {
            // The token endpoint is DISCOVERED from the user's own MCP server (PRM), so its response body
            // is response-controlled — never reflect it into the redirect. Log the detail server-side and
            // hand the browser a generic reason.
            loggerFactory.CreateLogger("Agent42.Mcp.OAuth").LogWarning(ex,
                "MCP OAuth code exchange failed for user {User}.", userId);
            return Results.Redirect("/?mcp_oauth=error&reason=token_exchange_failed");
        }

    }
}
