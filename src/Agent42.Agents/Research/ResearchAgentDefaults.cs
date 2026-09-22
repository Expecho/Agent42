namespace Agent42.Agents;

/// <summary>
/// The persona the research agent is seeded with (ADR 0082). Seeded once per user by
/// <see cref="IAgentRegistryGrain.EnsureResearchAgentAsync"/>; after that the user owns it and may edit
/// everything except its <see cref="AgentDefinition.Kind"/> and its per-chat history mode.
/// </summary>
public static class ResearchAgentDefaults
{
    public const string Name = "Research";

    public const string Description =
        "Plans a question into steps, searches and reads the web for each, and answers once the plan is done.";

    /// <summary>
    /// Only <see cref="ToolCatalog.WebSearch"/>, which covers both searching and reading a page. Deliberately NOT
    /// <see cref="ToolCatalog.ReadWebDocument"/>: a document ingested mid-run cannot reach retrieval until the
    /// turn ends (gotcha "A mid-run ingest cannot rebuild the agent"), and a whole research loop is one run.
    /// Seeding it would also bypass that key's consent stance. The user can still add it.
    /// </summary>
    public static IReadOnlyList<string> EnabledTools { get; } = [ToolCatalog.WebSearch];

    public const string SystemPrompt = """
        You are a careful research assistant. For every question:
        1. Plan first: break the question into a short list of concrete research steps and record them with
           todos_add before you search anything.
        2. Work through the steps one at a time. Search the web, open the most relevant pages, and mark each
           step done with todos_complete as soon as you have what it needs.
        3. If you learn that a step is wrong or unnecessary, remove it with todos_remove rather than leaving it
           open.
        4. When every step is done, write the answer: lead with the conclusion, then the supporting findings,
           and cite the pages you used as links. Say plainly what you could not confirm.
        Keep the plan small, usually three to six steps. Do not pad the answer with the plan itself.
        """;
}
