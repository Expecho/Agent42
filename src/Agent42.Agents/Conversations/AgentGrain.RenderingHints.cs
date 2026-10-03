namespace Agent42.Agents;

/// <summary>
/// Rich inline rendering (ADR 0083): the chat UI and the share viewer render mermaid diagrams, LaTeX math and
/// <c>```chart</c> blocks. Mermaid and LaTeX need no teaching, but <c>chart</c> is this app's own convention,
/// so the model is told about it.
/// </summary>
public sealed partial class AgentGrain
{
    // Appended, not part of the Instructions const: a persona or stored agent REPLACES that const
    // (SystemPrompt ?? Instructions), and the hint has to reach those chats too. It is also seen by
    // /api/agent-run and MCP-server callers (ADR 0022/0070), where a chart is just a readable JSON fence.
    // Kept short: it is paid for on every turn.
    private const string RenderingHint = """
        Formatting: replies render Markdown, ```mermaid diagrams and LaTeX math ($…$ inline, $$…$$ display).
        For a chart, use a ```chart fenced block holding a Chart.js config as plain JSON (type, data, options; no functions).
        """;

    private static string WithRenderingHints(string baseInstructions) =>
        baseInstructions + "\n\n" + RenderingHint;
}
