using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The MCP App routes (SEP-1865, ADR 0133): the frame a view renders in, the stored result a reloaded card replays
/// from, and the two calls a rendered view may make back to its own server. All four are ordinary authenticated
/// <c>/api</c> routes under the Easy Auth cookie; none is excluded, so none is public.
///
/// <para><b>The frame is untrusted third-party HTML served on our origin, and the protection is the RESPONSE
/// HEADER</b> (<see cref="McpAppCsp"/>), set before anything can fail so no path through the route, an error included,
/// renders without it. The registry is resolved with <c>GetService</c> (CLAUDE.md rule 7): a host that registers none
/// answers 404 instead of breaking route matching for every endpoint.</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapMcpAppEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/mcp-apps/{serverId}/frame", ServeMcpAppFrameAsync);

        // Owner-only by the grain key: the user id comes from the principal and the conversation id is only a key suffix,
        // so another user's call id resolves to a grain that has no such record.
        app.MapGet("/api/conversations/{conversationId}/mcp-apps/{callId}/result",
            async (string conversationId, string callId, IGrainFactory grains, HttpContext http) =>
            {
                AddMcpAppDataHeaders(http.Response);
                var json = await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetMcpAppResultAsync(callId);
                return json is null ? Results.NotFound() : Results.Text(json, "application/json; charset=utf-8");
            });

        app.MapPost("/api/mcp-apps/{serverId}/tools/call", async (string serverId, McpAppToolCallRequest request, HttpContext http) =>
        {
            AddMcpAppDataHeaders(http.Response);
            if (http.RequestServices.GetService<IMcpAppRegistry>() is not { } registry)
            {
                return Results.NotFound();
            }

            var outcome = await registry.CallToolAsync(
                http.GetUserId(), serverId, request.Name ?? "", request.Arguments, http.RequestAborted);
            return outcome.Ok ? Results.Text(outcome.Value.GetRawText(), "application/json; charset=utf-8") : McpAppFailure(outcome.Error);
        });

        app.MapPost("/api/mcp-apps/{serverId}/resources/read", async (string serverId, McpAppResourceReadRequest request, HttpContext http) =>
        {
            AddMcpAppDataHeaders(http.Response);
            if (http.RequestServices.GetService<IMcpAppRegistry>() is not { } registry)
            {
                return Results.NotFound();
            }

            var outcome = await registry.ReadUiResourceAsync(http.GetUserId(), serverId, request.Uri ?? "", http.RequestAborted);
            return outcome.Ok
                ? Results.Json(new McpAppResourceReadResponse(
                    [new McpAppResourceContents(request.Uri!, McpElicitationClient.AppsMimeType, outcome.Value!.Html)]))
                : McpAppFailure(outcome.Error);
        });
    }

    // Serves a view's HTML. The narrowest policy goes on FIRST and is only replaced, once the view is read, by the one
    // its own declared domains earn, so a 400, 404 or 502 carries the header too.
    private static async Task<IResult> ServeMcpAppFrameAsync(string serverId, string? uri, HttpContext http)
    {
        var response = http.Response;
        AddMcpAppDataHeaders(response);
        response.Headers.ContentSecurityPolicy = McpAppCsp.Default;

        if (http.RequestServices.GetService<IMcpAppRegistry>() is not { } registry)
        {
            return Results.NotFound();
        }

        var outcome = await registry.ReadUiResourceAsync(http.GetUserId(), serverId, uri ?? "", http.RequestAborted);
        if (!outcome.Ok)
        {
            return McpAppFailure(outcome.Error);
        }

        response.Headers.ContentSecurityPolicy = McpAppCsp.Build(outcome.Value!.UiMetaJson, OwnHosts(http.Request));
        return Results.Text(outcome.Value.Html, "text/html; charset=utf-8");
    }

    // The hostnames this request was addressed to, directly and behind the proxy, so a view can never declare us.
    private static List<string> OwnHosts(HttpRequest request)
    {
        var hosts = new List<string>();
        if (request.Host.HasValue)
        {
            hosts.Add(request.Host.Host);
        }

        foreach (var forwarded in request.Headers["X-Forwarded-Host"].SelectMany(v => (v ?? "").Split(',')))
        {
            var host = forwarded.Trim();
            var colon = host.LastIndexOf(':');
            hosts.Add(colon > 0 && !host.EndsWith(']') ? host[..colon] : host);
        }

        return [.. hosts.Where(h => h.Length > 0)];
    }

    private static void AddMcpAppDataHeaders(HttpResponse response)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "private, no-store";
    }

    // A curated status per failure, never the server's own text (it is third-party and may carry anything).
    private static IResult McpAppFailure(McpAppError error) => error switch
    {
        McpAppError.BadRequest => Results.BadRequest(),
        McpAppError.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        McpAppError.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
        McpAppError.Failed => Results.StatusCode(StatusCodes.Status502BadGateway),
        _ => Results.NotFound(),
    };
}

/// <summary>A view's <c>tools/call</c>: the tool and its JSON arguments.</summary>
public record McpAppToolCallRequest(string? Name, JsonElement? Arguments = null);

/// <summary>A view's <c>resources/read</c>: only a <c>ui://</c> uri is ever served.</summary>
public record McpAppResourceReadRequest(string? Uri);

/// <summary>The <c>resources/read</c> result shape the spec's view expects.</summary>
public record McpAppResourceReadResponse(IReadOnlyList<McpAppResourceContents> Contents);

public record McpAppResourceContents(string Uri, string MimeType, string Text);
