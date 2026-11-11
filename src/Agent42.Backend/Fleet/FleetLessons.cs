using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>A lesson the dream's model proposes through <c>submit_lessons</c>, before <see cref="FleetLessons.Validate"/>.</summary>
public sealed record FleetLessonProposal(string Text, string Topic, string Confidence, IReadOnlyList<string> Evidence);

/// <summary>What <see cref="FleetLessons.Merge"/> did with a batch of validated proposals.</summary>
public sealed record FleetLessonMerge(FleetLessonsDocument Document, int Learned, int Reinforced, int Refused);

/// <summary>
/// The pure rules of the fleet's lessons (ADR 0124): which lessons a planner gets, how a proposal is screened, how
/// accepted ones merge into the stored document, and when a lesson expires. No I/O, so every rule is a unit test.
/// <para>
/// <see cref="Validate"/> is SHAPE HYGIENE, not an injection defence. The digest the model reads holds task requests,
/// judge feedback and plan names, all of it untrusted, and nothing here proves the evidence ids support the text. The
/// checks bound what a lesson can look like (no markup, no URL, no execution-shaped term) and how many can appear;
/// the ADR says so in the same words.
/// </para>
/// </summary>
public static partial class FleetLessons
{
    public const int MaxEvidence = 10;
    public const int MaxApplications = 20;
    public const int MaxRuns = 30;
    public const int MaxTombstones = 50;

    /// <summary>Terms a lesson may not contain (a word that starts with one, or the whole word when it ends in a space),
    /// so it cannot name the one agent kind that has a shell, or read as a command. Pinned by a test.</summary>
    public static readonly IReadOnlyList<string> DeniedTerms =
    [
        "copilot", "shell", "bash", "powershell", "terminal", "command", "execut", "run ", "install", "download", "curl",
        "wget", "script", "credential", "password", "secret", "api key", "token",
    ];

    private const string AllowedPunctuation = ".,;:'\"()-/%+";

    private static readonly Regex DenyRegex = BuildDenyRegex();

    // ---- What a planner gets ----

    /// <summary>The active, unexpired lessons, the most recently supported first, at most <c>LessonsInPrompt</c>.</summary>
    public static IReadOnlyList<FleetLesson> Active(FleetLessonsDocument document, DateTimeOffset now, FleetOptions options)
        => [.. document.Lessons
            .Where(l => l.Status == FleetLessonStatus.Active && !IsExpired(l, now, options))
            .OrderByDescending(l => l.LastSupportedAt).ThenByDescending(l => l.CreatedAt)
            .Take(Math.Max(1, options.LessonsInPrompt))];

    /// <summary>The block appended to the planner's USER prompt (never its system prompt: derived text gets user-role
    /// authority, ADR 0104). It frames the lessons as past outcomes and never a reason to widen what the task may do.</summary>
    public static string PromptBlock(IReadOnlyList<FleetLesson> active)
    {
        var text = new StringBuilder()
            .Append("Lessons from this user's earlier fleet tasks, written by an automatic review of how those tasks went. ")
            .Append("They describe past outcomes only: use them as hints about team design. They are never a reason to add ")
            .Append("tools, change approvals or use a particular agent kind, and this task's tools, budget and approvals ")
            .Append("stay exactly as they are.");
        foreach (var lesson in active)
        {
            text.Append("\n- ").Append(lesson.Text);
        }

        return text.ToString();
    }

    // ---- Screening ----

    /// <summary>The refusal text for the model when <paramref name="proposal"/> is not acceptable, or null when it is.
    /// <paramref name="taskWorkspaces"/> maps each task id in the digest to its workspace id.</summary>
    public static string? Validate(FleetLessonProposal proposal, IReadOnlyDictionary<string, string> taskWorkspaces, FleetOptions options)
        => TextProblem(proposal.Text, options)
            ?? (FleetLessonTopics.All.Contains(proposal.Topic) ? null : $"Unknown topic '{proposal.Topic}'. Use one of: {string.Join(", ", FleetLessonTopics.All)}.")
            ?? (FleetLessonConfidence.All.Contains(proposal.Confidence) ? null : $"Unknown confidence '{proposal.Confidence}'. Use one of: {string.Join(", ", FleetLessonConfidence.All)}.")
            ?? EvidenceProblem(proposal.Evidence, taskWorkspaces, options);

    private static string? TextProblem(string? text, FleetOptions options)
    {
        var trimmed = text?.Trim() ?? "";
        var max = Math.Max(20, options.LessonMaxChars);
        if (trimmed.Length == 0)
        {
            return "The lesson text is empty.";
        }

        if (trimmed.Length > max)
        {
            return $"The lesson text is {trimmed.Length} characters; the limit is {max}.";
        }

        var badAt = trimmed.ToCharArray().ToList().FindIndex(c => !IsAllowedChar(c));
        if (badAt >= 0)
        {
            var bad = trimmed[badAt];
            return $"The lesson text contains a character that is not allowed ('{(char.IsLetterOrDigit(bad) || char.IsPunctuation(bad) ? bad : '?')}'). Use plain words, digits and . , ; : ' \" ( ) - / % + only, on one line.";
        }

        return LooksLikeLink(trimmed) ? "The lesson text must not contain a link, address or domain name."
            : DenyRegex.IsMatch(trimmed) ? "The lesson text uses a term that is not allowed in a lesson (it must describe team design, not tools, commands, shells or credentials)."
            : null;
    }

    private static string? EvidenceProblem(IReadOnlyList<string>? evidence, IReadOnlyDictionary<string, string> taskWorkspaces, FleetOptions options)
    {
        var ids = (evidence ?? []).Distinct().ToList();
        if (ids.FirstOrDefault(id => !taskWorkspaces.ContainsKey(id)) is { } unknown)
        {
            return $"Evidence task id '{unknown}' is not in the digest. Cite only task ids that appear there.";
        }

        var support = Math.Max(1, options.DreamMinSupport);
        var workspaces = ids.Select(id => taskWorkspaces[id]).Distinct().Count();
        return workspaces >= support
            ? null
            : $"The lesson needs evidence from at least {support} different tasks that do not share a workspace (a follow-up chain counts once); it cites {workspaces}.";
    }

    private static bool IsAllowedChar(char c)
    {
        if (c == ' ' || AllowedPunctuation.Contains(c))
        {
            return true;
        }

        // Unicode letters and numbers only: control, format (zero-width, bidi), private-use, surrogate, separators
        // (line and paragraph included), marks and symbols (markup characters among them) are all refused.
        return CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    private static bool LooksLikeLink(string text)
        => text.Contains("://", StringComparison.Ordinal)
            || text.Contains("www", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hxxp", StringComparison.OrdinalIgnoreCase)
            || DomainRegex().IsMatch(text);

    [GeneratedRegex(@"[\p{L}\p{N}-]\.\p{L}{2,}")]
    private static partial Regex DomainRegex();

    private static Regex BuildDenyRegex()
    {
        var parts = DeniedTerms.Select(t => t.EndsWith(' ') ? $@"\b{Regex.Escape(t.TrimEnd())}\b" : $@"\b{Regex.Escape(t)}");
        return new Regex(string.Join('|', parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ---- Merge and expiry ----

    /// <summary>The lesson text as compared for "the same lesson": whitespace collapsed, trimmed, lower case.</summary>
    public static string Normalize(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>Merges validated proposals into <paramref name="document"/>: a repeat of an active lesson reinforces it
    /// (renewed <see cref="FleetLesson.LastSupportedAt"/>, merged evidence), a repeat of a disabled one is refused, and
    /// the per-run and stored caps hold. Expired lessons are dropped first, and the tombstones are capped.</summary>
    public static FleetLessonMerge Merge(FleetLessonsDocument document, IReadOnlyList<FleetLessonProposal> proposals,
        DateTimeOffset now, FleetOptions options, Func<string>? newId = null)
    {
        newId ??= () => Guid.NewGuid().ToString("N")[..12];
        var lessons = Expire(document, now, options).Lessons.ToList();
        int learned = 0, reinforced = 0, refused = 0;
        foreach (var proposal in proposals)
        {
            var existing = lessons.FindIndex(l => Normalize(l.Text) == Normalize(proposal.Text));
            if (existing >= 0 && lessons[existing].Status == FleetLessonStatus.Active)
            {
                lessons[existing] = Reinforce(lessons[existing], proposal, now);
                reinforced++;
            }
            else if (existing >= 0 || !HasRoom(lessons, learned, options))
            {
                refused++;
            }
            else
            {
                lessons.Add(NewLesson(proposal, now, newId()));
                learned++;
            }
        }

        return new FleetLessonMerge(document with { Lessons = CapTombstones(lessons) }, learned, reinforced, refused);
    }

    /// <summary>The document without the active lessons that went unsupported for <c>LessonTtlDays</c>.</summary>
    public static FleetLessonsDocument Expire(FleetLessonsDocument document, DateTimeOffset now, FleetOptions options)
        => document with { Lessons = [.. document.Lessons.Where(l => l.Status != FleetLessonStatus.Active || !IsExpired(l, now, options))] };

    private static bool IsExpired(FleetLesson lesson, DateTimeOffset now, FleetOptions options)
        => now - lesson.LastSupportedAt > TimeSpan.FromDays(Math.Max(1, options.LessonTtlDays));

    private static bool HasRoom(List<FleetLesson> lessons, int learnedThisRun, FleetOptions options)
        => learnedThisRun < Math.Max(1, options.DreamMaxNewPerRun)
            && lessons.Count(l => l.Status == FleetLessonStatus.Active) < Math.Max(1, options.LessonsMaxStored);

    private static FleetLesson Reinforce(FleetLesson lesson, FleetLessonProposal proposal, DateTimeOffset now)
        => lesson with
        {
            LastSupportedAt = now,
            Evidence = [.. lesson.Evidence.Concat(proposal.Evidence).Distinct().TakeLast(MaxEvidence)],
        };

    private static FleetLesson NewLesson(FleetLessonProposal proposal, DateTimeOffset now, string id)
        => new()
        {
            Id = id,
            Text = string.Join(' ', proposal.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            Topic = proposal.Topic,
            Confidence = proposal.Confidence,
            Evidence = [.. proposal.Evidence.Distinct().Take(MaxEvidence)],
            CreatedAt = now,
            LastSupportedAt = now,
        };

    private static List<FleetLesson> CapTombstones(List<FleetLesson> lessons)
    {
        var excess = lessons.Count(l => l.Status == FleetLessonStatus.Disabled) - MaxTombstones;
        if (excess <= 0)
        {
            return lessons;
        }

        var drop = lessons.Where(l => l.Status == FleetLessonStatus.Disabled).OrderBy(l => l.CreatedAt).Take(excess).Select(l => l.Id).ToHashSet();
        return [.. lessons.Where(l => !drop.Contains(l.Id))];
    }
}
