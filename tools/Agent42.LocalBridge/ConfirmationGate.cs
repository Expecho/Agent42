using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agent42.LocalBridge;

/// <summary>
/// What the person at this machine answered. <see cref="AllowForSession"/> allows this call <i>and</i> every
/// later call of the same tool for as long as the helper runs — the runtime twin of <c>--auto-approve</c>.
/// </summary>
public enum ConfirmationAnswer
{
    /// <summary>Deny this call. The default for anything that is not an explicit yes.</summary>
    Deny = 0,

    /// <summary>Allow this call only; the next call of the same tool asks again.</summary>
    Allow,

    /// <summary>Allow this call and stop asking for this tool until the helper exits.</summary>
    AllowForSession,
}

/// <summary>What the person at this machine is asked. Implemented by the console; a seam for tests.</summary>
public interface IConfirmationPrompt
{
    /// <summary>
    /// Shows <paramref name="toolName"/> and <paramref name="arguments"/> — both already sanitized, the
    /// arguments in full — and returns what the user answered. Cancellation means "no answer".
    /// </summary>
    Task<ConfirmationAnswer> ConfirmAsync(string toolName, string arguments, CancellationToken cancellationToken);
}

/// <summary>The gate's verdict on one <c>tools/call</c>. A denial carries the text the model will read.</summary>
public sealed record GateDecision(bool Allowed, string? DenialText = null)
{
    public static readonly GateDecision Allow = new(true);

    public static GateDecision Deny(string text) => new(false, text);
}

/// <summary>
/// Decides, on the machine whose browser is being driven, whether a <c>tools/call</c> may reach the local
/// MCP server (ADR 0076). The backend relays whatever the model asks for; this is the only control.
///
/// <para><b>Fail closed.</b> A call passes unprompted only when the upstream's own <c>tools/list</c> said
/// <c>readOnlyHint: true</c> for that exact tool — an absent hint, <c>false</c>, an unknown tool and a call
/// before any <c>tools/list</c> all prompt. And "read-only" is only the upstream's claim: a read-only call
/// whose arguments contain anything URL-shaped prompts too, since a page load can carry data out.</para>
///
/// <para><b>…unless the person said so themselves.</b> A prompt answered <c>a</c>
/// (<see cref="ConfirmationAnswer.AllowForSession"/>) adds that tool to the same set <c>--auto-approve</c>
/// seeds, so it stops asking until the helper exits. The set is per process and never persisted: it dies
/// with the helper, and no relayed traffic can add to it — only a person typing at this console can.</para>
///
/// <para><b>The prompt text is attacker-influenced</b> (arguments are model-written, the model reads web
/// pages), so it is sanitized — control characters including ESC, and Unicode format characters such as
/// the bidi override U+202E — and shown <b>in full</b>. A prefix would let a payload hide behind a harmless
/// start; an argument set too large to review is denied instead.</para>
/// </summary>
public sealed partial class ConfirmationGate
{
    /// <summary>Largest argument text a person is asked to review. Beyond it the call is denied.</summary>
    public const int MaxReviewableArgChars = 20_000;

    public const string DeclinedText = "The user declined this browser action.";
    public const string TooLargeText = "The browser action was too large to review, so it was declined.";
    public const string TimedOutText = "The user did not answer in time, so the browser action was declined.";

    public static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions ReviewJson = new()
    {
        WriteIndented = true,
        // Readable for the reviewer (no é for é); safe because Sanitize runs over the result, and the
        // serializer still escapes every control character that sits inside a string value.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IConfirmationPrompt _prompt;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, bool> _readOnly = new(StringComparer.Ordinal);

    // Seeded from --auto-approve and grown only by an "a" answer at the console. Ordinal, like every other
    // tool-name comparison here: "Navigate" is a different tool from "navigate" as far as this gate knows.
    private readonly HashSet<string> _approved;

    public ConfirmationGate(IConfirmationPrompt prompt, IReadOnlySet<string>? autoApprove = null, TimeSpan? timeout = null)
    {
        _prompt = prompt;
        _approved = new HashSet<string>(autoApprove ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        _timeout = timeout ?? DefaultConfirmTimeout;
    }

    /// <summary>
    /// Learns tool annotations from a relayed <c>tools/list</c> result. Merged across channels and pages; a
    /// later listing overrides an earlier one for the same name.
    /// </summary>
    public void ObserveToolsList(JsonNode? result)
    {
        if (result?["tools"] is not JsonArray tools)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var tool in tools)
            {
                if (tool?["name"]?.GetValueKind() == JsonValueKind.String)
                {
                    var hint = tool["annotations"]?["readOnlyHint"];
                    _readOnly[tool["name"]!.GetValue<string>()] = hint?.GetValueKind() == JsonValueKind.True;
                }
            }
        }
    }

    /// <summary>Whether the upstream declared <paramref name="toolName"/> read-only — exactly <c>true</c>.</summary>
    public bool IsReadOnly(string toolName)
    {
        lock (_gate)
        {
            return _readOnly.TryGetValue(toolName, out var readOnly) && readOnly;
        }
    }

    /// <summary>Whether <paramref name="toolName"/> runs without asking — <c>--auto-approve</c> or an "a" answer.</summary>
    public bool IsApproved(string toolName)
    {
        lock (_gate)
        {
            return _approved.Contains(toolName);
        }
    }

    /// <summary>Reviews one call. Prompts are serialized: a second call waits for the first answer.</summary>
    public async Task<GateDecision> ReviewAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken)
    {
        if (IsApproved(toolName))
        {
            return GateDecision.Allow;
        }

        if (IsReadOnly(toolName) && !ContainsUrlLikeString(arguments))
        {
            return GateDecision.Allow;
        }

        var shown = Sanitize(arguments?.ToJsonString(ReviewJson) ?? "{}", keepNewlines: true);
        if (shown.Length > MaxReviewableArgChars)
        {
            return GateDecision.Deny(TooLargeText);
        }

        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            // Prompts are serialized, so the answer to the call ahead of this one may have just approved this
            // tool for the session. Asking again would be the very repetition "a" exists to end.
            if (IsApproved(toolName))
            {
                return GateDecision.Allow;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                var answer = await _prompt.ConfirmAsync(Sanitize(toolName, keepNewlines: false), shown, timeout.Token);
                if (answer == ConfirmationAnswer.AllowForSession)
                {
                    // The RAW name: that is what later calls arrive with. The sanitized one is for the eyes.
                    lock (_gate)
                    {
                        _approved.Add(toolName);
                    }
                }

                return answer == ConfirmationAnswer.Deny ? GateDecision.Deny(DeclinedText) : GateDecision.Allow;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return GateDecision.Deny(TimedOutText);
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Removes every character that can rewrite or reorder what a terminal shows: C0 and C1 controls
    /// (ESC among them), DEL, and Unicode format characters (Cf — the bidi overrides and isolates, zero-width
    /// joiners). Newlines survive only when asked, for the pretty-printed argument block.
    /// </summary>
    public static string Sanitize(string text, bool keepNewlines)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n' && keepNewlines)
            {
                builder.Append('\n');
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>True when any string anywhere in the arguments looks like a URL: <c>scheme:</c> or <c>//host</c>.</summary>
    public static bool ContainsUrlLikeString(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p => ContainsUrlLikeString(p.Value)),
        JsonArray array => array.Any(ContainsUrlLikeString),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => UrlLike().IsMatch(value.GetValue<string>().TrimStart()),
        _ => false,
    };

    /// <summary>
    /// The JSON-RPC <b>result</b> a denied call is answered with — a tool result with <c>isError</c>, not a
    /// protocol error, so the model reads the reason and can explain it.
    /// </summary>
    public static JsonObject DenialResult(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = true,
    };

    [GeneratedRegex("^(?:[A-Za-z][A-Za-z0-9+.-]*:|//)")]
    private static partial Regex UrlLike();
}
