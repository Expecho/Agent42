namespace Agent42.Agents;

/// <summary>
/// The fixed bounds of a research turn (ADR 0082). Constants rather than per-agent settings: the editor
/// exposes none of them (out of scope), and a constant is what a test can pin.
/// </summary>
public static class ResearchLimits
{
    /// <summary>
    /// The hard cap on how many times one research turn runs the model loop. No evaluator can exceed it:
    /// Agent Framework's <c>LoopAgent</c> stops here whatever the todos or the judge say. Each iteration can
    /// itself make several tool-calling model calls, so this bounds cost, not calls.
    /// </summary>
    public const int MaxIterations = 6;

    /// <summary>
    /// The context window the research prompt is compacted into, in tokens. Conservative on purpose:
    /// <c>ModelCapabilities</c> carries no window size, so this has to fit the smaller of the models a user
    /// might pick. A provider with a smaller window than this still fails the way any long chat does
    /// (gotcha "GitHub Models: the context-limit error is a 413"), which is a known limitation.
    /// </summary>
    public const int ContextWindowTokens = 64_000;

    /// <summary>The share of <see cref="ContextWindowTokens"/> kept free for the model's reply.</summary>
    public const int MaxOutputTokens = 8_000;

    /// <summary>
    /// The author stamped on the loop's own feedback messages ("you still have incomplete todos…"). Those are
    /// input to the model and never a user turn, so the history provider drops any <c>user</c> message with
    /// this author before storing it. Matching on the author as well as the role matters: assistant messages
    /// carry the agent's (user-editable) name, and a real user message carries none.
    /// </summary>
    public const string LoopAuthorName = "agent42-research-loop";

    /// <summary>
    /// What the AI judge checks on top of "was the original request answered". It is only asked once the
    /// todos are all done, because the loop's evaluators run in order and the first that wants another
    /// iteration wins.
    /// </summary>
    public static IReadOnlyList<string> JudgeCriteria { get; } =
    [
        "The answer states a conclusion rather than only listing what was found.",
        "The claims in the answer are supported by pages the agent actually searched or read.",
        "The answer says plainly what it could not confirm.",
    ];
}
