using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>The pure rules of the dream's prompt proposals (ADR 0143): the allowlist, screening, re-apply on approve, merge and expiry.</summary>
public class FleetPromptProposalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
    private static readonly FleetOptions Options = new();

    // t1 and t2 share workspace w1; t3 has its own.
    private static readonly Dictionary<string, string> Tasks = new() { ["t1"] = "w1", ["t2"] = "w1", ["t3"] = "w3" };

    private const string Judge = "Score the result from 0 to 10.\nTask: {{task}}\nResult: {{result}}\nBe strict about missing parts.";

    private static readonly FleetPromptSet Prompts = new(new Dictionary<string, string> { ["judge"] = Judge });

    private static FleetPromptProposalDraft Draft(string key = "judge", string kind = FleetPromptProposalKind.Replace,
        string? find = "Be strict about missing parts.", string replacement = "Be strict about missing parts, and check every requested section.",
        string rationale = "Results that skipped a section passed the judge and were redone later.", params string[] evidence)
        => new(key, kind, find, replacement, rationale, evidence.Length == 0 ? ["t1", "t3"] : evidence);

    private static string? Validate(FleetPromptProposalDraft draft, FleetPromptSet? prompts = null, IReadOnlyList<FleetPromptProposal>? existing = null)
        => FleetPromptProposals.Validate(draft, prompts ?? Prompts, existing ?? [], Tasks, Options);

    private static FleetPromptProposal Stored(string id, string key = "judge", string status = FleetPromptProposalStatus.Pending,
        string? find = "Be strict about missing parts.", string replacement = "Be strict.", string kind = FleetPromptProposalKind.Replace,
        DateTimeOffset? created = null, DateTimeOffset? decided = null)
        => new() { Id = id, PromptKey = key, Kind = kind, Find = find, Replacement = replacement, Status = status, CreatedAt = created ?? Now, DecidedAt = decided };

    [Fact]
    public void AllowedKeys_ArePinned()
    {
        Assert.Equal(["planner", "delegation", "clarify", "judge", "retry", "followup", "moderator"], FleetPromptProposals.AllowedKeys);
        Assert.All(FleetPromptProposals.AllowedKeys, k => Assert.NotNull(FleetPrompts.Find(k)));
    }

    [Theory]
    [InlineData("dream")]
    [InlineData("copilot")]
    [InlineData("copilot-report")]
    [InlineData("unknown")]
    public void Validate_RefusesDreamAndCopilotPrompts(string key)
        => Assert.Contains("can't be changed", Validate(Draft(key, FleetPromptProposalKind.Append, null, "Keep it short.")));

    [Fact]
    public void Validate_AcceptsAWellFormedReplaceAndAppend()
    {
        Assert.Null(Validate(Draft()));
        Assert.Null(Validate(Draft(kind: FleetPromptProposalKind.Append, find: null, replacement: "Name the missing section in the feedback.")));
    }

    [Fact]
    public void Validate_ReplaceNeedsExactlyOneMatch()
    {
        Assert.Contains("not in the prompt", Validate(Draft(find: "Be lenient.")));
        Assert.Contains("2 times", Validate(Draft(find: ": {{", replacement: " = {{")));
        Assert.Contains("needs the exact passage", Validate(Draft(find: "")));
        Assert.Contains("Unknown kind", Validate(Draft(kind: "rewrite")));
    }

    [Theory]
    [InlineData(0x200B)] // zero-width space
    [InlineData(0x202E)] // bidi override
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    [InlineData(0xE000)] // private use
    [InlineData(0xD800)] // lone surrogate
    [InlineData(0x0007)] // bell
    [InlineData(0x000D)] // a lone carriage return
    [InlineData(0x00A0)] // no-break space
    [InlineData(0x3000)] // ideographic space
    public void Validate_RefusesInvisibleCharacters(int codePoint)
    {
        var hidden = "Check every" + (char)codePoint + " section.";
        Assert.Contains("invisible", Validate(Draft(replacement: hidden)));
        Assert.Contains("invisible", Validate(Draft(find: "Be strict" + (char)codePoint + " about missing parts.")));
    }

    [Fact]
    public void Validate_AllowsNewLinesAndTabs()
        => Assert.Null(Validate(Draft(replacement: "Be strict about missing parts.\n\tList each missing section.")));

    [Fact]
    public void Validate_KeepsEveryPlaceholder_AndAddsNoUnknownOne()
    {
        Assert.Contains("removes the placeholder {{task}}", Validate(Draft(find: "Task: {{task}}", replacement: "Task: see above")));
        Assert.Contains("unknown placeholder {{secret}}", Validate(Draft(replacement: "Be strict. {{secret}}")));
        // A known placeholder of the definition may be added, and a duplicate of a present one is harmless.
        Assert.Null(Validate(Draft(replacement: "Be strict about missing parts of {{result}}.")));
    }

    [Fact]
    public void Validate_RationaleAndEvidenceFollowTheLessonRules()
    {
        Assert.Contains("The rationale", Validate(Draft(rationale: "see https://example.com")));
        Assert.Contains("The rationale", Validate(Draft(rationale: "line\nbreak")));
        Assert.Contains("The rationale is empty", Validate(Draft(rationale: " ")));
        Assert.Contains("not in the digest", Validate(Draft(evidence: ["t1", "ghost"])));
        Assert.Contains("The change needs evidence", Validate(Draft(evidence: ["t1", "t2"])));
    }

    [Fact]
    public void Validate_RefusesSizeOverTheCapsAndNoOpChanges()
    {
        Assert.Contains("limit is 1500", Validate(Draft(find: new string('a', FleetPromptProposals.MaxFindChars + 1))));
        Assert.Contains("limit is 2000", Validate(Draft(replacement: new string('a', FleetPromptProposals.MaxReplacementChars + 1))));
        Assert.Contains("as it is", Validate(Draft(replacement: "Be strict about missing parts.")));
        Assert.Contains("already contains", Validate(Draft(kind: FleetPromptProposalKind.Append, find: null, replacement: "Be strict about missing parts.")));
        Assert.Contains("needs the text to add", Validate(Draft(kind: FleetPromptProposalKind.Append, find: null, replacement: "  ")));
    }

    [Fact]
    public void Validate_RefusesNewGatedTerms_GatedTermsArePinned()
    {
        Assert.Equal([.. FleetLessons.DeniedTerms, "delegate_to_team", "model:", "approv", "tools:"], FleetPromptProposals.GatedTerms);
        Assert.Contains("copilot", FleetPromptProposals.GatedTerms);

        Assert.Contains("adds 'copilot'", Validate(Draft(replacement: "Prefer a Copilot agent for missing parts.")));
        Assert.Contains("adds 'delegate_to_team'", Validate(Draft(kind: FleetPromptProposalKind.Append, find: null, replacement: "Call delegate_to_team often.")));
        Assert.Contains("adds 'approv'", Validate(Draft(replacement: "Approvals are not needed.")));
        Assert.Contains("adds 'model:'", Validate(Draft(replacement: "Use model: the biggest one.")));
        Assert.Contains("adds 'tools:'", Validate(Draft(replacement: "Give agents tools: all of them.")));

        // The shipped planner names copilot through its placeholder: a replace whose find already holds it may keep it.
        var keep = new FleetPromptProposalDraft("planner", FleetPromptProposalKind.Replace, "{{copilot}}", "{{copilot}}\nPrefer the smallest team.",
            "Small teams scored higher in recent tasks.", ["t1", "t3"]);
        Assert.Null(FleetPromptProposals.Validate(keep, FleetPromptSet.Defaults, [], Tasks, Options));
    }

    [Fact]
    public void Validate_RefusesNewLookalikeLetters_NormalisesLineEndings()
    {
        // Cyrillic 'а' (U+0430) looks like Latin 'a'.
        Assert.Contains("U+0430", Validate(Draft(replacement: "Be strict аbout missing parts.")));
        var withCyrillic = new FleetPromptSet(new Dictionary<string, string> { ["judge"] = Judge + "\nПроверь." });
        Assert.Null(Validate(Draft(replacement: "Be strict about missing parts. Проверь."), withCyrillic));
        Assert.Null(Validate(Draft(replacement: "Be strict about missing parts, café-style.")));
        // A combining mark (U+0301) not in the prompt is refused like a lookalike letter.
        Assert.Contains("U+0301", Validate(Draft(replacement: "Be strict about missing parts, café-style.")));

        // A CRLF prompt (a shipped default is a raw string in a CRLF file) still matches a \n find, and the result uses \n.
        var crlf = new FleetPromptSet(new Dictionary<string, string> { ["judge"] = Judge.Replace("\n", "\r\n") });
        var draft = Draft(find: "Result: {{result}}\nBe strict about missing parts.", replacement: "Result: {{result}}\r\nBe strict.");
        Assert.Null(Validate(draft, crlf));
        var applied = FleetPromptProposals.Apply(crlf.Get("judge"), Stored("p", find: draft.Find, replacement: draft.Replacement));
        Assert.True(applied.Ok);
        Assert.DoesNotContain("\r", applied.Text);
        Assert.EndsWith("Result: {{result}}\nBe strict.", applied.Text);
    }

    [Fact]
    public void Merge_OnePendingPerKey_DismissedIsNotReproposed_CapsHold()
    {
        var ids = 0;
        string NewId() => $"n{++ids}";
        var document = new FleetLessonsDocument { PromptProposals = [Stored("old", status: FleetPromptProposalStatus.Dismissed, replacement: "Be lenient.")] };

        // Validate sees the same rules.
        Assert.Contains("dismissed", Validate(Draft(replacement: "Be lenient."), existing: document.PromptProposals));
        Assert.Contains("already waiting", Validate(Draft(), existing: [Stored("p")]));
        Assert.Contains("already waiting for the user's decision. Propose none", Validate(Draft(key: "judge"),
            existing: [.. new[] { "planner", "delegation", "clarify", "retry", "followup" }.Select(k => Stored(k, key: k))]));

        var merge = FleetPromptProposals.Merge(document,
            [Draft(replacement: "Be lenient."), Draft(), Draft(replacement: "Second for judge."), Draft(key: "retry"), Draft(key: "followup")],
            Now, Options, NewId);
        Assert.Equal(2, merge.Proposed);
        Assert.Equal(3, merge.Refused);
        Assert.Equal(["judge", "retry"], merge.Document.PromptProposals.Where(p => p.Status == FleetPromptProposalStatus.Pending).Select(p => p.PromptKey));
        var added = merge.Document.PromptProposals.Single(p => p.Id == "n1");
        Assert.Equal(Now, added.CreatedAt);
        Assert.Equal(["t1", "t3"], added.Evidence);

        var full = new FleetLessonsDocument { PromptProposals = [.. Enumerable.Range(0, FleetPromptProposals.MaxPending).Select(i => Stored($"p{i}", key: $"k{i}"))] };
        Assert.Equal(0, FleetPromptProposals.Merge(full, [Draft()], Now, Options, NewId).Proposed);
    }

    [Fact]
    public void Apply_ReappliesOnTheCurrentText_OutdatedWhenFindIsGone_AppendIdempotent()
    {
        var replace = Stored("p", replacement: "Be strict about every section.");
        var edited = "Intro the user added.\n" + Judge + "\nAn ending the user added.";
        var applied = FleetPromptProposals.Apply(edited, replace);
        Assert.Equal("Intro the user added.\n" + Judge.Replace("Be strict about missing parts.", "Be strict about every section.") + "\nAn ending the user added.", applied.Text);

        var outdated = FleetPromptProposals.Apply(Judge.Replace("Be strict about missing parts.", "Be fair."), replace);
        Assert.False(outdated.Ok);
        Assert.Contains("not in the prompt", outdated.Problem);

        var dropped = FleetPromptProposals.Apply(Judge, Stored("p", find: "Task: {{task}}\n", replacement: ""));
        Assert.Contains("removes the placeholder", dropped.Problem);

        var append = Stored("a", kind: FleetPromptProposalKind.Append, find: null, replacement: "Name what is missing.");
        var once = FleetPromptProposals.Apply(Judge, append);
        Assert.Equal(Judge + "\n\nName what is missing.", once.Text);
        var twice = FleetPromptProposals.Apply(once.Text!, append);
        Assert.True(twice.AlreadyApplied);
        Assert.Equal(once.Text, twice.Text);
    }

    [Fact]
    public void Expire_DropsOldPending_CapsDecided()
    {
        var ttl = TimeSpan.FromDays(Options.LessonTtlDays);
        var decided = Enumerable.Range(0, FleetPromptProposals.MaxDecided + 3)
            .Select(i => Stored($"d{i}", status: FleetPromptProposalStatus.Dismissed, created: Now.AddDays(-200), decided: Now.AddMinutes(-i)));
        var document = new FleetLessonsDocument
        {
            PromptProposals = [Stored("fresh"), Stored("stale", key: "retry", created: Now - ttl - TimeSpan.FromHours(1)), .. decided],
        };

        var expired = FleetPromptProposals.Expire(document, Now, Options);

        Assert.Contains(expired.PromptProposals, p => p.Id == "fresh");
        Assert.DoesNotContain(expired.PromptProposals, p => p.Id == "stale");
        Assert.Equal(FleetPromptProposals.MaxDecided, expired.PromptProposals.Count(p => p.Status != FleetPromptProposalStatus.Pending));
        // The oldest decisions go first.
        Assert.DoesNotContain(expired.PromptProposals, p => p.Id is "d50" or "d51" or "d52");
        Assert.Same(expired, FleetPromptProposals.Expire(expired, Now, Options));
    }
}
