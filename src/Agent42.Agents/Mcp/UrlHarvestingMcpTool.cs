using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Wraps one tool of a URL-seeding MCP server (ADR 0067) so the absolute http(s) URLs in its result become
/// fetchable by <c>fetch_web_page</c> and readable by <c>read_web_document</c> in the conversation that
/// received them. <see cref="DelegatingAIFunction"/> forwards Name/Description/JsonSchema unchanged, so the
/// model's view of the tool is identical — only a side effect is added, and the result passes through
/// untouched.
///
/// <para><b>It lives in the grain layer, and is constructed per conversation, deliberately.</b>
/// <c>TruncatingMcpTool</c> in the Backend already wraps every MCP tool and sees its result — the obvious
/// seam, and the wrong one: <c>UserMcpToolProvider</c> is a per-USER singleton whose wrapped tool instances
/// are shared across every conversation of that user, while the fetchable-URL lists are per-CONVERSATION
/// grain state. Harvesting there would leak one conversation's discovered URLs into another. The
/// <paramref name="harvest"/> callback closes over the grain, so this wrapper is conversation-scoped by
/// construction.</para>
///
/// <para><b>This sits OUTSIDE the truncation wrapper</b> (<c>BuildAgent</c> wraps what the provider already
/// wrapped), so the text handed to <paramref name="harvest"/> is the already-truncated result — see
/// <c>AgentGrain.RememberSeededUrlsFromToolResult</c>, which drops a URL cut off at that boundary rather
/// than seeding its prefix.</para>
/// </summary>
/// <param name="inner">The already-truncation-wrapped MCP tool.</param>
/// <param name="serverId">The seeding server's id — logged with every harvest, so the provenance the
/// separate seeded list records is actually readable after the fact (ADR 0027's auditability rule).</param>
/// <param name="harvest">Receives (serverId, toolName, serialized result) after every successful call.</param>
internal sealed class UrlHarvestingMcpTool(
    AIFunction inner,
    string serverId,
    Action<string, string, string> harvest) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = await base.InvokeCoreAsync(arguments, cancellationToken);

        // MCP results surface as a JsonElement; ToString() yields its compact JSON — the same reading
        // TruncatingMcpTool does, so both wrappers see the same text.
        harvest(serverId, Name, raw as string ?? raw?.ToString() ?? "");

        // The result is returned UNCHANGED: this wrapper adds a side effect, never a transformation. A
        // failing call throws through it and seeds nothing, which is correct — there is no result to trust.
        return raw;
    }
}
