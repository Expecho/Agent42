using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.DevHost;

/// <summary>
/// The client tools and shared state (ADR 0089), offline. Each trigger phrase makes the canned model call one tool;
/// the reply after it quotes the tool's result, and "what is in the shared state" quotes the state the model was
/// shown, so a user edit in the panel is visible in the next reply. Kept in its own part so
/// <see cref="SelectToolCall"/> stays under S3776.
/// </summary>
public sealed partial class CannedChatClient
{
    /// <summary>The model asks the user to approve an action (confirm_action).</summary>
    public const string ConfirmTrigger = "ask me to confirm";

    /// <summary>The model reads the browser's context (get_browser_context).</summary>
    public const string BrowserContextTrigger = "browser context";

    /// <summary>The model opens this chat's latest artifact in the pane (control_artifact_pane).</summary>
    public const string ShowArtifactTrigger = "show the artifact";

    /// <summary>The model maximizes the pane.</summary>
    public const string FullscreenTrigger = "pane fullscreen";

    /// <summary>The model closes the pane.</summary>
    public const string ClosePaneTrigger = "close the pane";

    /// <summary>The model writes a checklist into the shared state (update_shared_state).</summary>
    public const string ChecklistTrigger = "make a checklist";

    /// <summary>The model ticks the first checklist item.</summary>
    public const string TickTrigger = "tick the first item";

    /// <summary>The reply quotes the shared state the model was shown this run.</summary>
    public const string StateQuestionTrigger = "what is in the shared state";

    private static FunctionCallContent? ClientToolCall(
        IReadOnlyList<ChatMessage> list, int lastUserIndex, string text, ChatOptions? options)
    {
        var (name, args) = text switch
        {
            _ when Has(text, ConfirmTrigger) => (ClientToolLimits.ConfirmAction,
                new Dictionary<string, object?> { ["action"] = "Send the quarterly report to the whole team." }),
            _ when Has(text, BrowserContextTrigger) => (ClientToolLimits.GetBrowserContext, []),
            _ when Has(text, ShowArtifactTrigger) && LastArtifactId(list, lastUserIndex) is { } id =>
                (ClientToolLimits.ControlArtifactPane, new Dictionary<string, object?> { ["action"] = "open", ["artifactId"] = id }),
            _ when Has(text, FullscreenTrigger) => (ClientToolLimits.ControlArtifactPane,
                new Dictionary<string, object?> { ["action"] = "fullscreen" }),
            _ when Has(text, ClosePaneTrigger) => (ClientToolLimits.ControlArtifactPane,
                new Dictionary<string, object?> { ["action"] = "close" }),
            _ when Has(text, ChecklistTrigger) => (SharedStateLimits.UpdateToolName, ChecklistPatch()),
            _ when Has(text, TickTrigger) => (SharedStateLimits.UpdateToolName, Patch(
                new { op = "replace", path = "/checklist/0/done", value = true })),
            _ => ((string?)null, (Dictionary<string, object?>?)null),
        };

        return name is not null && options?.Tools?.Any(t => t.Name == name) is true
            ? new FunctionCallContent($"canned-{name}-{Guid.NewGuid():N}", name, args)
            : null;
    }

    // Prefixed to the reply: the result of this turn's tool call, and the shared state when asked about it.
    private static string ClientToolNote(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var lastUser = LastUserIndex(list);
        var result = list.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        var note = result is null ? "" : $"The tool answered: `{result.Result}`\n\n";

        var instructions = options?.Instructions ?? "";
        var state = instructions.IndexOf("## Shared state", StringComparison.Ordinal);
        if (lastUser >= 0 && Has(list[lastUser].Text ?? "", StateQuestionTrigger))
        {
            note += state >= 0
                ? "I was shown this shared state:\n\n" + instructions[state..] + "\n\n"
                : "I was shown no shared state.\n\n";
        }

        return note;
    }

    private static bool Has(string text, string trigger) => text.Contains(trigger, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> ChecklistPatch() => Patch(new
    {
        op = "add",
        path = "/checklist",
        value = new[]
        {
            new { id = "venue", title = "Book the venue", done = false },
            new { id = "invites", title = "Send the invitations", done = false },
            new { id = "cake", title = "Order the cake", done = false },
        },
    });

    // update_shared_state takes its operations as a JSON array; a real provider's arguments are JsonElements.
    private static Dictionary<string, object?> Patch(params object[] operations) => new()
    {
        ["operations"] = System.Text.Json.JsonSerializer.SerializeToElement(operations),
    };
}
