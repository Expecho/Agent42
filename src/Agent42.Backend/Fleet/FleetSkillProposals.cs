using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>A skill change the dream's model proposes through <c>propose_skill_change</c>, before
/// <see cref="FleetSkillProposals.Validate"/>. For a create, <see cref="Replacement"/> is the whole body.</summary>
public sealed record FleetSkillProposalDraft(
    string SkillName, string Kind, string? Find, string Replacement, string? Description, string Rationale, IReadOnlyList<string> Evidence);

/// <summary>One of the user's installed skills as the dream sees it: its name, its description and the full current <c>SKILL.md</c>.</summary>
public sealed record FleetSkillSnapshot(string Name, string Description, string Markdown);

/// <summary>What <see cref="FleetSkillProposals.Apply"/> made of an edit on the current <c>SKILL.md</c>: the new text, or why it no
/// longer applies. <see cref="AlreadyApplied"/> means the body already holds the edit (an append written before).</summary>
public sealed record FleetSkillApply(string? Markdown, string? Problem, bool AlreadyApplied = false)
{
    public bool Ok => Problem is null;
}

/// <summary>What <see cref="FleetSkillProposals.Merge"/> did with a run's accepted skill proposals.</summary>
public sealed record FleetSkillProposalMerge(FleetLessonsDocument Document, int Proposed, int Refused);

/// <summary>
/// The pure rules of the dream's skill proposals (ADR 0146), the sibling of <see cref="FleetPromptProposals"/>: which skills may
/// be edited or created, how a proposal is screened, how an edit is re-applied to the CURRENT <c>SKILL.md</c> on approve, and
/// how proposals merge and expire. No I/O.
/// <para>
/// The control is the user's Approve (ADR 0143), not these checks; they keep that review honest. Unlike a prompt proposal there
/// is NO gated-term rule: the user decided the dream may write skills whose guidance covers shell-capable tools. The frontmatter
/// is code-built (create) or frozen (edit): the model supplies a name, a one-line description and a body, never the YAML.
/// </para>
/// </summary>
public static class FleetSkillProposals
{
    /// <summary>Evidence from at least this many different workspaces. Higher than the lesson threshold: a skill is meant to be
    /// reused, so a one-off must not become one. Applied as <c>Math.Max(DreamMinSupport, MinSupport)</c>.</summary>
    public const int MinSupport = 3;

    public const int MaxPerRun = 2;
    public const int MaxPending = 5;
    public const int MaxFindChars = 1_500;
    public const int MaxReplacementChars = 2_000;
    public const int MaxBodyChars = 6_000;
    public const int MaxDecided = 50;

    private const string Fence = "---";
    private const string DescriptionPunctuation = ".,;()-/%+";

    /// <summary>Every shipped skill's name plus every companion name: none may be edited, and a creation may not take one (an
    /// upload of that name silently replaces the shipped skill, and <c>websearch</c> holds the ADR 0011 mitigation text).</summary>
    public static IReadOnlySet<string> ShippedNames()
        => SkillCatalog.Skills.Select(s => s.Name).Concat(ToolCatalog.CompanionSkills.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---- Screening ----

    /// <summary>The refusal text for the model when <paramref name="draft"/> is not acceptable against the user's
    /// <paramref name="installed"/> skills and the stored <paramref name="existing"/> proposals, or null when it is.</summary>
    public static string? Validate(FleetSkillProposalDraft draft, IReadOnlyList<FleetSkillSnapshot> installed, IReadOnlySet<string> shippedNames,
        IReadOnlyList<FleetSkillProposal> existing, IReadOnlyDictionary<string, string> taskWorkspaces, FleetOptions options)
    {
        if (!FleetSkillProposalKind.All.Contains(draft.Kind))
        {
            return $"Unknown kind '{draft.Kind}'. Use replace, append or create.";
        }

        var problem = draft.Kind == FleetSkillProposalKind.Create
            ? CreateProblem(draft, installed, shippedNames)
            : EditProblem(draft, installed, shippedNames);
        return problem
            ?? FleetLessons.TextProblem(draft.Rationale, options, "The rationale")
            ?? FleetLessons.EvidenceProblem(draft.Evidence, taskWorkspaces, options, "A skill change", MinSupport)
            ?? ExistingProblem(draft, existing);
    }

    private static string? NameProblem(string name, IReadOnlySet<string> shippedNames)
        => shippedNames.Contains(name) ? $"'{name}' is the name of a built-in skill. A built-in skill can't be changed or replaced."
            : !SkillPackage.IsSafeSkillName(name) ? "The skill name must be lowercase letters, digits and single hyphens (at most 64 characters)."
            : null;

    private static string? CreateProblem(FleetSkillProposalDraft draft, IReadOnlyList<FleetSkillSnapshot> installed, IReadOnlySet<string> shippedNames)
    {
        var body = FleetPromptProposals.Normalize(draft.Replacement ?? "").Trim();
        var description = draft.Description ?? "";
        if (NameProblem(draft.SkillName, shippedNames) is { } name)
        {
            return name;
        }

        if (installed.Any(s => string.Equals(s.Name, draft.SkillName, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A skill named '{draft.SkillName}' is already installed. Propose a replace or append to it instead.";
        }

        if (installed.Count >= SkillToolLimits.MaxInstalledSkillsPerUser)
        {
            return $"The user already has {SkillToolLimits.MaxInstalledSkillsPerUser} skills installed, the most allowed. Propose no new skill.";
        }

        var markdown = BuildMarkdown(draft.SkillName, description, body);
        var baseline = string.Join('\n', installed.Select(s => s.Markdown));
        return DescriptionProblem(description)
            ?? (body.Length == 0 ? "A new skill needs its body in replacement." : null)
            ?? (body.Length > MaxBodyChars ? $"The body is {body.Length} characters; the limit is {MaxBodyChars}." : null)
            ?? FleetPromptProposals.InvisibleProblem(body, "replacement")
            ?? (markdown.Length > SkillToolLimits.MaxSkillBodyLength ? SizeProblem(markdown.Length) : null)
            ?? FleetPromptProposals.LookalikeProblem(body + description, baseline);
    }

    private static string? EditProblem(FleetSkillProposalDraft draft, IReadOnlyList<FleetSkillSnapshot> installed, IReadOnlySet<string> shippedNames)
    {
        // The name rule wins over "uploaded only": an upload that shadows a shipped name is not an edit target either.
        if (shippedNames.Contains(draft.SkillName))
        {
            return NameProblem(draft.SkillName, shippedNames);
        }

        if (installed.FirstOrDefault(s => string.Equals(s.Name, draft.SkillName, StringComparison.OrdinalIgnoreCase)) is not { } target)
        {
            return $"'{draft.SkillName}' is not one of the user's installed skills. Use one of: {string.Join(", ", installed.Select(s => s.Name))}.";
        }

        var current = FleetPromptProposals.Normalize(target.Markdown);
        var find = FleetPromptProposals.Normalize(draft.Find ?? "");
        var replacement = FleetPromptProposals.Normalize(draft.Replacement ?? "");
        return EditShapeProblem(draft.Kind, find, replacement) ?? EditResultProblem(draft.Kind, find, replacement, current);
    }

    private static string? EditShapeProblem(string kind, string find, string replacement)
        => kind == FleetSkillProposalKind.Replace && find.Length == 0 ? "A replace needs the exact passage to replace in find."
            : kind == FleetSkillProposalKind.Append && replacement.Trim().Length == 0 ? "An append needs the text to add in replacement."
            : find.Length > MaxFindChars ? $"find is {find.Length} characters; the limit is {MaxFindChars}. Replace a shorter passage."
            : replacement.Length > MaxReplacementChars ? $"replacement is {replacement.Length} characters; the limit is {MaxReplacementChars}."
            : FleetPromptProposals.InvisibleProblem(find, "find") ?? FleetPromptProposals.InvisibleProblem(replacement, "replacement");

    private static string? EditResultProblem(string kind, string find, string replacement, string current)
    {
        var applied = Apply(current, kind, find, replacement);
        return !applied.Ok ? applied.Problem
            : applied.AlreadyApplied ? "The skill already contains that text."
            : applied.Markdown == current ? "The change leaves the skill as it is."
            : applied.Markdown!.Length > SkillToolLimits.MaxSkillBodyLength ? SizeProblem(applied.Markdown.Length)
            : FleetPromptProposals.LookalikeProblem(replacement, current);
    }

    private static string SizeProblem(int length)
        => $"The skill would be {length} characters; the limit is {SkillToolLimits.MaxSkillBodyLength} (the whole SKILL.md, frontmatter included), "
            + "or load_skill truncates it. Make it shorter.";

    // One plain line the YAML frontmatter can carry without quoting: letters, digits, spaces and . , ; ( ) - / % + only. That
    // keeps out ': ', '#', quotes, brackets and anything else YAML gives meaning to, so a description can't smuggle in a key.
    private static string? DescriptionProblem(string description)
    {
        var text = description.Trim();
        var max = FleetSkillTools.MaxListedDescriptionLength;
        if (text.Length == 0)
        {
            return "A new skill needs a description.";
        }

        if (text.Length > max)
        {
            return $"The description is {text.Length} characters; the limit is {max}.";
        }

        if (!char.IsLetterOrDigit(text[0]))
        {
            return "The description must start with a letter or digit.";
        }

        if (text.Contains(Fence, StringComparison.Ordinal))
        {
            return "The description must not contain '---'.";
        }

        var bad = text.FirstOrDefault(c => !(char.IsLetterOrDigit(c) || c == ' ' || DescriptionPunctuation.Contains(c)));
        return bad != default
            ? $"The description contains a character that is not allowed ('{(char.IsPunctuation(bad) ? bad : '?')}'). Use one line of plain words, digits and {DescriptionPunctuation} only, no colon or quote."
            : FleetPromptProposals.InvisibleProblem(text, "description");
    }

    /// <summary>The refusal when <paramref name="draft"/> clashes with the stored proposals: another pending one for the same
    /// skill, a repeat of a dismissed one, or too many pending. Checked by the dream and again by the merge.</summary>
    public static string? ExistingProblem(FleetSkillProposalDraft draft, IReadOnlyList<FleetSkillProposal> existing)
        => existing.Any(p => p.Status == FleetPromptProposalStatus.Pending && SameName(p.SkillName, draft.SkillName))
            ? $"A change to '{draft.SkillName}' is already waiting for the user's decision. Propose at most one per skill."
            : existing.Any(p => p.Status == FleetPromptProposalStatus.Dismissed && SameEdit(p, draft))
            ? "The user dismissed this change before. Do not propose it again."
            : existing.Count(p => p.Status == FleetPromptProposalStatus.Pending) >= MaxPending
            ? $"{MaxPending} skill changes are already waiting for the user's decision. Propose none now."
            : null;

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameEdit(FleetSkillProposal p, FleetSkillProposalDraft draft)
        => SameName(p.SkillName, draft.SkillName) && p.Kind == draft.Kind
            && FleetLessons.Normalize(p.Find ?? "") == FleetLessons.Normalize(draft.Find ?? "")
            && FleetLessons.Normalize(p.Replacement) == FleetLessons.Normalize(draft.Replacement ?? "");

    // ---- Build and apply ----

    /// <summary>The <c>SKILL.md</c> of a created skill: frontmatter built by code from a validated name and description, then the body.</summary>
    public static string BuildMarkdown(string name, string description, string body)
        => $"---\nname: {name}\ndescription: {description.Trim()}\n---\n\n{FleetPromptProposals.Normalize(body).Trim()}\n";

    /// <summary>
    /// An edit <paramref name="proposal"/> re-applied to <paramref name="currentMarkdown"/>, the skill's <c>SKILL.md</c> as it is
    /// NOW. The edit touches the BODY only (the text after the closing <c>---</c>): the frontmatter is kept as it is, once line
    /// endings become <c>\n</c>. A replace needs its passage exactly once (else outdated); an append whose text already ends the
    /// body is already applied.
    /// </summary>
    public static FleetSkillApply Apply(string currentMarkdown, FleetSkillProposal proposal)
        => Apply(FleetPromptProposals.Normalize(currentMarkdown), proposal.Kind,
            FleetPromptProposals.Normalize(proposal.Find ?? ""), FleetPromptProposals.Normalize(proposal.Replacement));

    private static FleetSkillApply Apply(string current, string kind, string find, string replacement)
    {
        if (kind == FleetSkillProposalKind.Create)
        {
            return new FleetSkillApply(null, "A new skill is installed, not applied to an existing one.");
        }

        if (SplitFrontmatter(current) is not var (head, body))
        {
            return new FleetSkillApply(null, "The skill's SKILL.md has no frontmatter to keep, so it can't be edited.");
        }

        if (kind == FleetSkillProposalKind.Append)
        {
            var add = replacement.TrimEnd();
            return add.Length > 0 && body.TrimEnd().EndsWith(add, StringComparison.Ordinal)
                ? new FleetSkillApply(current, null, AlreadyApplied: true)
                : new FleetSkillApply(head + body.TrimEnd() + "\n\n" + add + "\n", null);
        }

        var count = find.Length == 0 ? 0 : FleetPromptProposals.Occurrences(body, find);
        return count == 1 ? new FleetSkillApply(head + FleetPromptProposals.ReplaceOnce(body, find, replacement), null)
            : count == 0 ? new FleetSkillApply(null, "The passage to replace is not in the skill (any more). find must match the skill's text exactly.")
            : new FleetSkillApply(null, $"The passage to replace occurs {count} times in the skill; find must match exactly once. Quote a longer passage.");
    }

    /// <summary>Splits a <c>SKILL.md</c> into its frontmatter (through the closing fence line and its newline) and its body, or
    /// null when it does not open with a fenced frontmatter block.</summary>
    internal static (string Head, string Body)? SplitFrontmatter(string markdown)
    {
        var text = markdown.TrimStart('﻿');
        if (!text.StartsWith(Fence + "\n", StringComparison.Ordinal))
        {
            return null;
        }

        var offset = markdown.Length - text.Length;
        for (var at = text.IndexOf("\n" + Fence, Fence.Length, StringComparison.Ordinal); at >= 0;
            at = text.IndexOf("\n" + Fence, at + 1, StringComparison.Ordinal))
        {
            var end = at + 1 + Fence.Length;
            if (end == text.Length || text[end] == '\n')
            {
                var headEnd = end == text.Length ? end : end + 1;
                return (markdown[..(offset + headEnd)], markdown[(offset + headEnd)..]);
            }
        }

        return null;
    }

    // ---- Merge and expiry ----

    /// <summary>
    /// Adds a run's accepted drafts to <paramref name="document"/> as pending skill proposals, re-checking the stored ones (the
    /// user may have decided one while the model ran) and the per-run cap. Expired proposals are dropped first.
    /// </summary>
    public static FleetSkillProposalMerge Merge(FleetLessonsDocument document, IReadOnlyList<FleetSkillProposalDraft> drafts,
        DateTimeOffset now, FleetOptions options, Func<string>? newId = null)
    {
        newId ??= () => Guid.NewGuid().ToString("N")[..12];
        var proposals = Expire(document, now, options).SkillProposals.ToList();
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

        return new FleetSkillProposalMerge(document with { SkillProposals = proposals }, proposed, refused);
    }

    /// <summary>The document without pending skill proposals older than <c>LessonTtlDays</c>, and with at most
    /// <see cref="MaxDecided"/> decided ones (the oldest decisions dropped first).</summary>
    public static FleetLessonsDocument Expire(FleetLessonsDocument document, DateTimeOffset now, FleetOptions options)
    {
        var ttl = TimeSpan.FromDays(Math.Max(1, options.LessonTtlDays));
        var kept = document.SkillProposals.Where(p => p.Status != FleetPromptProposalStatus.Pending || now - p.CreatedAt <= ttl).ToList();
        var excess = kept.Count(p => p.Status != FleetPromptProposalStatus.Pending) - MaxDecided;
        if (excess > 0)
        {
            var drop = kept.Where(p => p.Status != FleetPromptProposalStatus.Pending)
                .OrderBy(p => p.DecidedAt ?? p.CreatedAt).Take(excess).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            kept = [.. kept.Where(p => !drop.Contains(p.Id))];
        }

        return kept.Count == document.SkillProposals.Count ? document : document with { SkillProposals = kept };
    }

    private static FleetSkillProposal NewProposal(FleetSkillProposalDraft draft, DateTimeOffset now, string id)
        => new()
        {
            Id = id,
            SkillName = draft.SkillName,
            Kind = draft.Kind,
            Find = draft.Kind == FleetSkillProposalKind.Replace ? FleetPromptProposals.Normalize(draft.Find ?? "") : null,
            Replacement = FleetPromptProposals.Normalize(draft.Replacement ?? ""),
            Description = draft.Kind == FleetSkillProposalKind.Create ? draft.Description?.Trim() : null,
            Rationale = string.Join(' ', draft.Rationale.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            Evidence = [.. draft.Evidence.Distinct().Take(FleetLessons.MaxEvidence)],
            CreatedAt = now,
        };
}
