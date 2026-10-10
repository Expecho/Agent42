using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Shows an MCP elicitation to the browser of ONE interactive run (ADR 0095): the run's
/// <see cref="ClientToolBroker"/> queues it, the stream loop emits it as an <c>mcpElicitation</c> event, and the
/// browser answers through the same <c>tool-result</c> endpoint as a client tool. Built by the grain only for a run
/// whose browser advertised <see cref="ClientToolLimits.McpElicitation"/>; every other run has no sink.
///
/// <para>Every way the wait can fail answers <c>cancel</c>: a closed queue (the run is over), the
/// <paramref name="wait"/> elapsing, the call's token, an answer over <see cref="ClientToolLimits.MaxResultChars"/>
/// (never truncated: cut JSON is corrupt), an unreadable answer, or content that does not match the schema.</para>
/// </summary>
public sealed class McpElicitationSink(ClientToolBroker broker, TimeSpan wait, ILogger logger) : IMcpElicitationSink
{
    public async Task<McpElicitationAnswer> AskAsync(
        string serverName, string toolCallId, McpElicitationRequest request, CancellationToken cancellationToken)
    {
        var id = $"elicit_{Guid.NewGuid():N}";
        var input = InputJson(id, serverName, request);
        if (input.Length > McpElicitationLimits.MaxRequestChars)
        {
            logger.LogWarning("MCP elicitation from {Server} of {Length} chars refused as oversized; answering cancel",
                serverName, input.Length);
            return McpElicitationAnswer.Cancel;
        }

        var answer = broker.Elicitations.TryEnqueue(new PendingMcpElicitation(id, toolCallId, serverName, input));
        if (answer is null)
        {
            return McpElicitationAnswer.Cancel;
        }

        try
        {
            return Accept(request, await answer.WaitAsync(wait, cancellationToken));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return McpElicitationAnswer.Cancel;
        }
        finally
        {
            broker.Elicitations.Forget(id);
        }
    }

    /// <summary>
    /// What the browser renders: <c>{id, mode, server, message, schema?, url?, host?}</c>. The schema is embedded
    /// as an object, the URL only after <see cref="McpElicitationLimits.TryValidateUrl"/> (the router refused a bad one
    /// already, so this is the second check, not the first).
    /// </summary>
    public static string InputJson(string id, string serverName, McpElicitationRequest request)
    {
        var input = new JsonObject
        {
            ["id"] = id,
            ["mode"] = request.Mode,
            ["server"] = serverName,
            ["message"] = request.Message,
        };
        if (request.Mode == McpElicitationLimits.FormMode && request.SchemaJson is { } schema)
        {
            input["schema"] = JsonNode.Parse(schema);
        }

        if (request.Mode == McpElicitationLimits.UrlMode && McpElicitationLimits.TryValidateUrl(request.Url, out var uri))
        {
            input["url"] = uri!.AbsoluteUri;
            input["host"] = uri.Host;
        }

        return input.ToJsonString();
    }

    // The browser's {action, content?} as the server's answer, or cancel when it is not one this request allows.
    private McpElicitationAnswer Accept(McpElicitationRequest request, string raw)
    {
        if (raw.Length > ClientToolLimits.MaxResultChars)
        {
            logger.LogWarning("MCP elicitation answer of {Length} chars refused as oversized; answering cancel", raw.Length);
            return McpElicitationAnswer.Cancel;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var action = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("action", out var a)
                && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (action != McpElicitationLimits.Accept)
            {
                return action == McpElicitationLimits.Decline
                    ? new McpElicitationAnswer(McpElicitationLimits.Decline)
                    : McpElicitationAnswer.Cancel;
            }

            return AcceptContent(request, root.TryGetProperty("content", out var content) ? content.GetRawText() : null);
        }
        catch (JsonException)
        {
            return McpElicitationAnswer.Cancel;
        }
    }

    // URL mode accepts with no content (the user opened the page); form mode only with content that fits the schema.
    private McpElicitationAnswer AcceptContent(McpElicitationRequest request, string? contentJson)
    {
        if (request.Mode == McpElicitationLimits.UrlMode)
        {
            return new McpElicitationAnswer(McpElicitationLimits.Accept);
        }

        if (!McpElicitationContent.IsValid(request.SchemaJson, contentJson))
        {
            logger.LogWarning("MCP elicitation answer does not match the requested schema; answering cancel");
            return McpElicitationAnswer.Cancel;
        }

        return new McpElicitationAnswer(McpElicitationLimits.Accept, contentJson);
    }
}
