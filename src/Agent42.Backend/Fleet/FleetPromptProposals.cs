using System.Globalization;
using System.Text.RegularExpressions;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>A prompt change the dream's model proposes through <c>propose_prompt_change</c>, before
/// <see cref="FleetPromptProposals.Validate"/>.</summary>
public sealed record FleetPromptProposalDraft(
    string PromptKey, string Kind, string? Find, string Replacement, string Rationale, IReadOnlyList<string> Evidence);

/// <summary>What <see cref="FleetPromptProposals.Apply"/> made of a proposal on the current text: the new text, or why it no
/// longer applies. <see cref="AlreadyApplied"/> means the text already holds the edit (an append that was written before).</summary>
public sealed record FleetPromptApply(string? Text, string? Problem, bool AlreadyApplied = false)
{
    public bool Ok => Problem is null;
}

/// <summary>What <see cref="FleetPromptProposals.Merge"/> did with a run's accepted proposals.</summary>
public sealed record FleetPromptProposalMerge(FleetLessonsDocument Document, int Proposed, int Refused);

/// <summary>
/// The pure rules of the dream's prompt proposals (ADR 0143): which prompts may be proposed, how a proposal is screened,
/// how it is re-applied to the CURRENT prompt on approve, and how proposals merge and expire. No I/O, like
/// <see cref="FleetLessons"/>.
/// <para>
/// The control is the user's Approve, not these checks. They keep that review HONEST: no invisible character, no letter
/// that only looks Latin, no placeholder lost or invented, and no gated term (an agent kind, delegation, tools, models,
/// approvals) that the replaced text did not already contain, so a proposal can't steer the planner towards wider powers
/// in a diff that reads harmlessly.
/// </para>
/// </summary>
public static class FleetPromptProposals
{
    /// <summary>The prompts the dream may propose a change to. An allowlist, so a prompt added later stays out by default:
    /// never <c>dream</c> (its own instructions), <c>copilot</c> or <c>copilot-report</c> (the agent kind with a shell).</summary>
    public static readonly IReadOnlyList<string> AllowedKeys = ["planner", "delegation", "clarify", "judge", "retry", "followup", "moderator"];

    /// <summary>Terms a replacement may only contain when the text it replaces already does (an append: never). Pinned by a test.</summary>
    public static readonly IReadOnlyList<string> GatedTerms = [.. FleetLessons.DeniedTerms, "delegate_to_team", "model:", "approv", "tools:"];

    public const int MaxPerRun = 2;
    public const int MaxPending = 5;
    public const int MaxFindChars = 1_500;
    public const int MaxReplacementChars = 2_000;
    public const int MaxDecided = 50;

    private static readonly IReadOnlyList<(string Term, Regex Regex)> GatedRegexes = [.. GatedTerms.Select(t => (t, FleetLessons.TermRegex([t])))];

    // ---- Screening ----

    /// <summary>The refusal text for the model when <paramref name="draft"/> is not acceptable against the user's current
    /// <paramref name="prompts"/> and the stored <paramref name="existing"/> proposals, or null when it is.</summary>
    public static string? Validate(FleetPromptProposalDraft draft, FleetPromptSet prompts, IReadOnlyList<FleetPromptProposal> existing,
        IReadOnlyDictionary<string, string> taskWorkspaces, FleetOptions options)
    {
        if (!AllowedKeys.Contains(draft.PromptKey))
        {
            return $"Prompt '{draft.PromptKey}' can't be changed by a proposal. Use one of: {string.Join(", ", AllowedKeys)}.";
        }

        var current = Normalize(prompts.Get(draft.PromptKey));
        return ShapeProblem(draft)
            ?? ResultProblem(draft, current)
            ?? FleetLessons.TextProblem(draft.Rationale, options, "The rationale")
            ?? FleetLessons.EvidenceProblem(draft.Evidence, taskWorkspaces, options, "The change")
            ?? ExistingProblem(draft, existing);
    }

    private static string? ShapeProblem(FleetPromptProposalDraft draft)
    {
        if (!FleetPromptProposalKind.All.Contains(draft.Kind))
        {
            return $"Unknown kind '{draft.Kind}'. Use replace or append.";
        }

        var find = Normalize(draft.Find ?? "");
        var replacement = Normalize(draft.Replacement ?? "");
        return draft.Kind == FleetPromptProposalKind.Replace && find.Length == 0 ? "A replace needs the exact passage to replace in find."
            : draft.Kind == FleetPromptProposalKind.Append && replacement.Trim().Length == 0 ? "An append needs the text to add in replacement."
            : find.Length > MaxFindChars ? $"find is {find.Length} characters; the limit is {MaxFindChars}. Replace a shorter passage."
            : replacement.Length > MaxReplacementChars ? $"replacement is {replacement.Length} characters; the limit is {MaxReplacementChars}."
            : InvisibleProblem(find, "find") ?? InvisibleProblem(replacement, "replacement");
    }

    internal static string? InvisibleProblem(string text, string field)
    {
        var at = text.ToList().FindIndex(IsInvisible);
        return at < 0 ? null
            : $"{field} contains an invisible or control character (U+{(int)text[at]:X4} at {at}). Use visible text, new lines and tabs only.";
    }

    // Control (except new line and tab), format (zero-width, bidi), private-use, surrogates (emoji among them), line and
    // paragraph separators, unassigned code points and every space but the plain one (no-break and wide spaces): anything
    // that could hide in a diff the user reviews.
    internal static bool IsInvisible(char c)
        => c is not ('\n' or '\t' or ' ') && CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.SpaceSeparator;

    // The edit on the current text: find exactly once, a different and placeholder-whole result, no new gated term and no
    // new non-Latin letter.
    private static string? ResultProblem(FleetPromptProposalDraft draft, string current)
    {
        var find = Normalize(draft.Find ?? "");
        var replacement = Normalize(draft.Replacement);
        if (draft.Kind == FleetPromptProposalKind.Append && current.Contains(replacement, StringComparison.Ordinal))
        {
            return "The prompt already contains that text.";
        }

        var applied = Apply(current, draft.PromptKey, draft.Kind, find, replacement);
        if (!applied.Ok)
        {
            return applied.Problem;
        }

        return applied.Text == current ? "The change leaves the prompt as it is."
            : GatedProblem(draft.Kind == FleetPromptProposalKind.Replace ? find : "", replacement)
            ?? LookalikeProblem(replacement, current);
    }

    private static string? GatedProblem(string replaced, string replacement)
    {
        var added = GatedRegexes.FirstOrDefault(g => g.Regex.IsMatch(replacement) && !g.Regex.IsMatch(replaced));
        return added.Term is null ? null
            : $"The replacement adds '{added.Term.Trim()}', which the text it replaces does not contain. A proposal may not add agent kinds, "
              + "tools, models, delegation, approvals or commands; the user changes those in the prompt editor.";
    }

    internal static string? LookalikeProblem(string replacement, string current)
    {
        // Combining marks count too: one can turn a Latin letter into a lookalike.
        var bad = replacement.FirstOrDefault(c => (char.IsLetter(c) || IsMark(c)) && c > 'ɏ' && !current.Contains(c));
        return bad == default ? null
            : $"The replacement uses the letter or mark U+{(int)bad:X4}, which is not Latin and does not occur in the prompt. Use Latin letters.";
    }

    private static bool IsMark(char c)
        => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>The refusal when <paramref name="draft"/> clashes with the stored proposals: another pending one for the same
    /// prompt, a repeat of a dismissed one, or too many pending. Checked by the dream and again by the merge.</summary>
    public static string? ExistingProblem(FleetPromptProposalDraft draft, IReadOnlyList<FleetPromptProposal> existing)
        => existing.Any(p => p.Status == FleetPromptProposalStatus.Pending && p.PromptKey == draft.PromptKey)
            ? $"A change to '{draft.PromptKey}' is already waiting for the user's decision. Propose at most one per prompt."
            : existing.Any(p => p.Status == FleetPromptProposalStatus.Dismissed && SameEdit(p, draft))
            ? "The user dismissed this change before. Do not propose it again."
            : existing.Count(p => p.Status == FleetPromptProposalStatus.Pending) >= MaxPending
            ? $"{MaxPending} changes are already waiting for the user's decision. Propose none now."
            : null;

    private static bool SameEdit(FleetPromptProposal p, FleetPromptProposalDraft draft)
        => p.PromptKey == draft.PromptKey && p.Kind == draft.Kind
            && FleetLessons.Normalize(p.Find ?? "") == FleetLessons.Normalize(draft.Find ?? "")
            && FleetLessons.Normalize(p.Replacement) == FleetLessons.Normalize(draft.Replacement);

    // ---- Apply ----

    /// <summary>
    /// <paramref name="proposal"/> re-applied to <paramref name="currentText"/>, the user's prompt as it is NOW. A replace needs
    /// its passage exactly once (else it is outdated); an append whose text already ends the prompt is already applied, which
    /// makes approve idempotent. The result must keep every placeholder and add no unknown one. Line endings become <c>\n</c>.
    /// </summary>
    public static FleetPromptApply Apply(string currentText, FleetPromptProposal proposal)
        => Apply(Normalize(currentText), proposal.PromptKey, proposal.Kind, Normalize(proposal.Find ?? ""), Normalize(proposal.Replacement));

    private static FleetPromptApply Apply(string current, string key, string kind, string find, string replacement)
    {
        if (kind == FleetPromptProposalKind.Append)
        {
            return current.EndsWith(replacement, StringComparison.Ordinal) && replacement.Length > 0
                ? new FleetPromptApply(current, null, AlreadyApplied: true)
                : Checked(current.TrimEnd() + "\n\n" + replacement, current, key);
        }

        var count = Occurrences(current, find);
        return count == 1 ? Checked(ReplaceOnce(current, find, replacement), current, key)
            : count == 0 ? new FleetPromptApply(null, "The passage to replace is not in the prompt (any more). find must match the prompt text exactly.")
            : new FleetPromptApply(null, $"The passage to replace occurs {count} times in the prompt; find must match exactly once. Quote a longer passage.");
    }

    private static FleetPromptApply Checked(string result, string current, string key)
    {
        var before = FleetPrompts.PlaceholdersIn(current);
        var after = FleetPrompts.PlaceholdersIn(result);
        var known = FleetPrompts.Find(key)?.Placeholders.Keys ?? [];
        if (before.FirstOrDefault(p => !after.Contains(p)) is { } lost)
        {
            return new FleetPromptApply(null, $"The change removes the placeholder {{{{{lost}}}}}; every placeholder must stay.");
        }

        return after.FirstOrDefault(p => !before.Contains(p) && !known.Contains(p)) is { } unknown
            ? new FleetPromptApply(null, $"The change adds the unknown placeholder {{{{{unknown}}}}}.")
            : new FleetPromptApply(result, null);
    }

    internal static int Occurrences(string text, string find)
    {
        var count = 0;
        for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    internal static string ReplaceOnce(string text, string find, string replacement)
    {
        var at = text.IndexOf(find, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + find.Length));
    }

    /// <summary>Line endings as <c>\n</c>: a shipped default is a C# raw string and keeps the source file's line endings.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- Merge and expiry ----

    /// <summary>
    /// Adds a run's accepted drafts to <paramref name="document"/> as pending proposals, re-checking the stored ones (the user
    /// may have decided one while the model ran) and the per-run cap. Expired proposals are dropped first.
    /// </summary>
    public static FleetPromptProposalMerge Merge(FleetLessonsDocument document, IReadOnlyList<FleetPromptProposalDraft> drafts,
        DateTimeOffset now, FleetOptions options, Func<string>? newId = null)
    {
        newId ??= () => Guid.NewGuid().ToString("N")[..12];
        var proposals = Expire(document, now, options).PromptProposals.ToList();
        int proposed = 0, refused = 0;
        foreach (var draft in drafts)
        {
            if (proposed >= MaxPerRun || ExistingProblem(draft, proposals) is not null)
            {
                refused++;
                continue;
            }

            proposals.Add(NewProposal(draft, now, newId()));
            proposed++;
        }

        return new FleetPromptProposalMerge(document with { PromptProposals = proposals }, proposed, refused);
    }

    /// <summary>The document without pending proposals older than <c>LessonTtlDays</c>, and with at most
    /// <see cref="MaxDecided"/> decided ones (the oldest decisions dropped first).</summary>
    public static FleetLessonsDocument Expire(FleetLessonsDocument document, DateTimeOffset now, FleetOptions options)
    {
        var ttl = TimeSpan.FromDays(Math.Max(1, options.LessonTtlDays));
        var kept = document.PromptProposals.Where(p => p.Status != FleetPromptProposalStatus.Pending || now - p.CreatedAt <= ttl).ToList();
        var excess = kept.Count(p => p.Status != FleetPromptProposalStatus.Pending) - MaxDecided;
        if (excess > 0)
        {
            var drop = kept.Where(p => p.Status != FleetPromptProposalStatus.Pending)
                .OrderBy(p => p.DecidedAt ?? p.CreatedAt).Take(excess).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            kept = [.. kept.Where(p => !drop.Contains(p.Id))];
        }

        return kept.Count == document.PromptProposals.Count ? document : document with { PromptProposals = kept };
    }

    private static FleetPromptProposal NewProposal(FleetPromptProposalDraft draft, DateTimeOffset now, string id)
        => new()
        {
            Id = id,
            PromptKey = draft.PromptKey,
            Kind = draft.Kind,
            Find = draft.Kind == FleetPromptProposalKind.Replace ? Normalize(draft.Find ?? "") : null,
            Replacement = Normalize(draft.Replacement),
            Rationale = string.Join(' ', draft.Rationale.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            Evidence = [.. draft.Evidence.Distinct().Take(FleetLessons.MaxEvidence)],
            CreatedAt = now,
        };
}
