using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The compaction half of <c>read_document</c> (ADR 0098). The tool itself is <see cref="ReadDocumentTool"/>; these static
/// helpers stay in the grain because they share <c>ToolText</c> with the other compaction branches (ADR 0148).
/// </summary>
public sealed partial class AgentGrain
{
    // Compaction's read_document branch (ADR 0098): a SUCCESSFUL part (it starts with PartHeaderPrefix) becomes a
    // stub built from the call's own arguments. A refusal read nothing and stays verbatim, and a stub no longer
    // starts with the header, so a second pass leaves it alone.
    private static void CompactReadDocumentResult(FunctionCallContent call, FunctionResultContent? result)
    {
        if (result is null
            || ToolText(result.Result) is not { } text
            || !text.StartsWith(ReadDocumentLimits.PartHeaderPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var fileName = call.Arguments?.TryGetValue("fileName", out var name) == true ? ToolText(name) ?? "" : "";
        var part = call.Arguments?.TryGetValue("part", out var value) == true ? PartArgument(value) : 1;
        result.Result = ReadDocumentLimits.Stub(fileName, part);
    }

    // The call's `part`: a JsonElement from the wire (ToolText gives its raw text), a boxed int in-process. Missing or
    // unparsable means 1, the tool's default.
    private static int PartArgument(object? value)
        => value is int i ? i : int.TryParse(ToolText(value), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
}
