using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The grain's half of the past-chat tools (ADR 0112): compaction of their results after the turn. The tools
/// themselves are <see cref="PastChatTools"/>; this stays here because it shares <c>ToolText</c> and
/// <c>PartArgument</c> with the other compaction branches in <c>CompactToolPayloads</c>.
/// </summary>
public sealed partial class AgentGrain
{
    // Compaction's past-chat branch (ADR 0112): a SUCCESSFUL result (it starts with its header prefix) becomes a stub
    // built from the call's own arguments. A refusal stays verbatim, and a stub no longer starts with the header, so a
    // second pass leaves it alone (the CompactReadDocumentResult shape).
    private static void CompactPastChatResult(FunctionCallContent call, FunctionResultContent? result)
    {
        if (result is null || ToolText(result.Result) is not { } text)
        {
            return;
        }

        if (call.Name == PastChatLimits.SearchToolName && text.StartsWith(PastChatLimits.SearchHeaderPrefix, StringComparison.Ordinal))
        {
            result.Result = PastChatLimits.SearchStub(Argument(call, "query"));
        }
        else if (call.Name == PastChatLimits.ReadToolName && text.StartsWith(PastChatLimits.ReadHeaderPrefix, StringComparison.Ordinal))
        {
            // PartArgument reads a wire JsonElement or an in-process boxed int, as read_document's part does.
            var ordinal = call.Arguments?.TryGetValue("ordinal", out var value) == true ? PartArgument(value) : 0;
            result.Result = PastChatLimits.ReadStub(Argument(call, "conversationId"), ordinal);
        }
    }

    private static string Argument(FunctionCallContent call, string name)
        => call.Arguments?.TryGetValue(name, out var value) == true ? ToolText(value) ?? "" : "";
}
