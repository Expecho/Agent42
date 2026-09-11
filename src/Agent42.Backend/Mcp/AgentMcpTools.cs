using System.ComponentModel;
using Agent42.Agents;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agent42.Backend;

/// <summary>
/// The tool surface an external MCP client sees (ADR 0070): one tool, <c>ask_agent</c>. Thin by design —
/// every rule lives in <see cref="AgentMcpRunner"/> and the credential in <see cref="AgentMcpKeyGate"/>.
///
/// <para><b>The descriptions here ARE the contract the calling model reads.</b> They are serialized into
/// the JSON schema returned by <c>tools/list</c> on every connection, which is the only place a remote
/// model learns the conversation-id rule and the prompt budget. ADR 0067's lesson applies in the mirror
/// image: there, a rule written only into a skill file never reached the model; here, a rule written only
/// into an ADR never reaches the caller.</para>
///
/// <para>Not a static class: <c>WithTools&lt;T&gt;</c> takes a type argument, and a static type cannot be
/// one.</para>
/// </summary>
[McpServerToolType]
public sealed class AgentMcpTools
{
    [McpServerTool(Name = "ask_agent")]
    [Description(
        "Ask the Agent 42 agent this API key belongs to a question, and get its reply. The agent runs "
        + "with its own persona, tools and documents. The turn is recorded in the owner's chat history "
        + "and is visible to them. Omit conversationId to start a new conversation; pass back the "
        + "conversationId from a previous reply to continue that one. Only an id this tool returned is "
        + "accepted. The call blocks until the agent finishes and may take a minute or more.")]
    public static async Task<CallToolResult> AskAgentAsync(
        IHttpContextAccessor httpContextAccessor,
        IGrainFactory grains,
        ILogger<AgentMcpTools> logger,
        [Description("The question or instruction to send to the agent.")]
        string prompt,
        [Description("The conversationId from an earlier ask_agent reply, to continue that conversation.")]
        string? conversationId = null)
    {
        // Resolved once, by the gate, before routing — never re-derived here. The accessor reaches the
        // live HttpContext because the transport runs stateless, which is its default (measured on
        // ModelContextProtocol.AspNetCore 2.2.0).
        if (httpContextAccessor.HttpContext?.Items[AgentMcpKeyGate.CallerItemKey] is not AgentMcpCaller caller)
        {
            // Unreachable while the gate is installed. It is kept as defence in depth precisely because
            // that is a wiring fact, not a property of this method: a host that mapped the surface and
            // forgot the middleware would otherwise serve every exposed agent anonymously.
            // Fully qualified rather than imported: the SDK root namespace carries several
            // Mcp*-prefixed types and this project already has its own (McpConnectionFactory,
            // McpSecretProtector, McpOAuthClient) in a flat namespace (C7).
            throw new ModelContextProtocol.McpException(AgentMcpKeyGate.RejectionBody);
        }

        var result = await AgentMcpRunner.RunAsync(caller, prompt, conversationId, grains, logger: logger);

        var text = result.ConversationId.Length > 0
            ? $"conversationId: {result.ConversationId}\n\n{result.Text}"
            : result.Text;

        // A refusal and a timeout ride back IN BAND — as a CallToolResult with IsError set — rather
        // than as a JSON-RPC error response. The two are not the same thing and conflating them loses
        // the point: a protocol error is a transport failure the calling model can only surface to its
        // user, while IsError is the model-readable signal that lets it act on "pass back this
        // conversationId" or "the prompt was too long". Returning a bare string would make every
        // refusal arrive as isError:false, indistinguishable from a reply.
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = result.IsError,
        };
    }
}
