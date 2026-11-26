using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>The pure rules of the dream's skill proposals (ADR 0146): targets, screening, the body-only re-apply, merge and expiry.</summary>
public class FleetSkillProposalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 1, 0, 0, TimeSpan.Zero);
    private static readonly FleetOptions Options = new();
    private static readonly IReadOnlySet<string> Shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "websearch", "scheduledprompt", "docs" };

    // t1 and t2 share workspace w1; t3 and t4 have their own. Three workspaces need t1/t2 + t3 + t4.
    private static readonly Dictionary<string, string> Tasks = new() { ["t1"] = "w1", ["t2"] = "w1", ["t3"] = "w3", ["t4"] = "w4" };

    private static string Md(string name = "deploys", string body = "Run the build.\nThen run the tests.\nReport the result.")
        => FleetSkillProposals.BuildMarkdown(name, "How to deploy this repo.", body);

    private static IReadOnlyList<FleetSkillSnapshot> Installed(params FleetSkillSnapshot[] more)
        => [new("deploys", "How to deploy this repo.", Md()), .. more];

    private static FleetSkillProposalDraft Edit(string skill = "deploys", string kind = FleetSkillProposalKind.Replace,
        string? find = "Then run the tests.", string replacement = "Then run the tests and the linter.", params string[] evidence)
        => new(skill, kind, find, replacement, null, "Runs that skipped the linter were redone later in three tasks.",
            evidence.Length == 0 ? ["t1", "t3", "t4"] : evidence);

    private static FleetSkillProposalDraft Create(string name = "release-notes", string description = "How to write release notes for this repo.",
        string body = "Group changes by area.\nLink each pull request.", params string[] evidence)
        => new(name, FleetSkillProposalKind.Create, null, body, description, "Every task wrote release notes the same way.",
            evidence.Length == 0 ? ["t1", "t3", "t4"] : evidence);

    private static string? Validate(FleetSkillProposalDraft draft, IReadOnlyList<FleetSkillSnapshot>? installed = null,
        IReadOnlyList<FleetSkillProposal>? existing = null)
        => FleetSkillProposals.Validate(draft, installed ?? Installed(), Shipped, existing ?? [], Tasks, Options);

    private static FleetSkillProposal Stored(string id, string skill = "deploys", string status = FleetPromptProposalStatus.Pending,
        string kind = FleetSkillProposalKind.Replace, string? find = "Then run the tests.", string replacement = "Then run the tests and the linter.",
        DateTimeOffset? created = null, DateTimeOffset? decided = null)
        => new() { Id = id, SkillName = skill, Kind = kind, Find = find, Replacement = replacement, Status = status, CreatedAt = created ?? Now, DecidedAt = decided };

    [Fact]
    public void SkillProposals_RoundTrip_OlderDocumentReads()
    {
        var document = new FleetLessonsDocument
        {
            SkillProposals = [Stored("p1") with { Description = "d" }],
            Runs = [new FleetDreamRun { Id = "r1", SkillsProposed = 2 }],
        };

        var back = JsonSerializer.Deserialize<FleetLessonsDocument>(JsonSerializer.Serialize(document))!;
        Assert.Equal("p1", back.SkillProposals.Single().Id);
        Assert.Equal("d", back.SkillProposals.Single().Description);
        Assert.Equal(2, back.Runs.Single().SkillsProposed);

        // A document written before ADR 0146 has neither field.
        var older = JsonSerializer.Deserialize<FleetLessonsDocument>("""{"Lessons":[],"Runs":[{"Id":"r0","Proposed":1}],"PromptProposals":[]}""")!;
        Assert.Empty(older.SkillProposals);
        Assert.Equal(0, older.Runs.Single().SkillsProposed);
    }

    [Fact]
    public void Validate_AcceptsAWellFormedReplaceAppendAndCreate()
    {
        Assert.Null(Validate(Edit()));
        Assert.Null(Validate(Edit(kind: FleetSkillProposalKind.Append, find: null, replacement: "Always post the build link.")));
        Assert.Null(Validate(Create()));
    }

    [Theory]
    [InlineData("websearch")]
    [InlineData("scheduledprompt")]
    [InlineData("docs")]
    public void Validate_RefusesShippedAndCompanionTargets(string name)
    {
        var installed = Installed(new FleetSkillSnapshot(name, "x", Md(name)));
        Assert.Contains("built-in", Validate(Edit(name), installed));
    }

    [Fact]
    public void Validate_RefusesShadowingUploadAsTarget()
    {
        // An upload named websearch exists in the user's skills, but the name rule wins: it is not an edit target.
        var installed = Installed(new FleetSkillSnapshot("websearch", "Mine.", Md("websearch", "Mine.")));
        Assert.Contains("built-in", Validate(Edit("websearch", find: "Mine.", replacement: "Mine, changed."), installed));
        Assert.Contains("not one of the user's installed skills", Validate(Edit("nothere")));
    }

    [Fact]
    public void Validate_Create_RefusesShippedAndExistingNames()
    {
        Assert.Contains("built-in", Validate(Create("docs")));
        Assert.Contains("built-in", Validate(Create("WebSearch")));
        Assert.Contains("already installed", Validate(Create("deploys")));
        Assert.Contains("lowercase", Validate(Create("../evil")));
        Assert.Contains("lowercase", Validate(Create("Has Space")));
    }

    [Fact]
    public void Validate_Create_RefusesWhenTheUserIsAtTheInstallCap()
    {
        var full = Enumerable.Range(0, SkillToolLimits.MaxInstalledSkillsPerUser).Select(i => new FleetSkillSnapshot($"s{i}", "d", Md($"s{i}"))).ToList();
        Assert.Contains("most allowed", Validate(Create(), full));
    }

    [Fact]
    public void Validate_NeedsThreeWorkspaces()
    {
        // t1 and t2 share a workspace, so two of them plus t3 is only two workspaces.
        Assert.Contains("at least 3", Validate(Edit(evidence: ["t1", "t2", "t3"])));
        Assert.Contains("at least 3", Validate(Create(evidence: ["t1", "t3"])));
        Assert.Null(Validate(Edit(evidence: ["t1", "t3", "t4"])));
        Assert.Contains("not in the digest", Validate(Edit(evidence: ["t1", "t3", "zz"])));
    }

    [Fact]
    public void Validate_ScreensText_AndSize()
    {
        Assert.Contains("invisible", Validate(Edit(replacement: "Then run​ the tests.")));
        Assert.Contains("invisible", Validate(Edit(replacement: "Then run the tests \U0001F680.")));
        Assert.Contains("invisible", Validate(Edit(replacement: "Then run the tests.")));
        Assert.Contains("not Latin", Validate(Edit(replacement: "Then run the tеsts.")));
        Assert.Contains("limit is " + FleetSkillProposals.MaxFindChars, Validate(Edit(find: new string('a', FleetSkillProposals.MaxFindChars + 1))));
        Assert.Contains("limit is " + FleetSkillProposals.MaxReplacementChars, Validate(Edit(replacement: new string('a', FleetSkillProposals.MaxReplacementChars + 1))));
        Assert.Contains("limit is " + FleetSkillProposals.MaxBodyChars, Validate(Create(body: new string('a', FleetSkillProposals.MaxBodyChars + 1))));

        // The whole SKILL.md counts against 16 KiB, frontmatter included. An append grows a file that ends in one newline by
        // text.Length + 2, so exactly on the limit is fine and one character over is not.
        var empty = FleetSkillProposals.BuildMarkdown("big", "How to deploy this repo.", "").Length;
        var big = FleetSkillProposals.BuildMarkdown("big", "How to deploy this repo.", new string('x', SkillToolLimits.MaxSkillBodyLength - 1_500 - empty));
        var installed = Installed(new FleetSkillSnapshot("big", "How to deploy this repo.", big));
        var room = SkillToolLimits.MaxSkillBodyLength - big.Length - 2;
        Assert.Null(Validate(Edit("big", FleetSkillProposalKind.Append, null, new string('y', room)), installed));
        Assert.Contains("limit is " + SkillToolLimits.MaxSkillBodyLength, Validate(Edit("big", FleetSkillProposalKind.Append, null, new string('y', room + 1)), installed));
    }

    [Theory]
    [InlineData("Line one.\nLine two.")]
    [InlineData("Line one.\rname: other")]
    [InlineData("Does this: work")]
    [InlineData("Say \"hi\" to it")]
    [InlineData("It's a skill")]
    [InlineData("A skill --- with a fence")]
    [InlineData("- starts with a dash")]
    [InlineData("Has # a comment")]
    [InlineData("")]
    public void Validate_Description_RefusesYamlAndInjection(string description)
        => Assert.NotNull(Validate(Create(description: description)));

    [Fact]
    public void Validate_Description_RefusesOverLong()
        => Assert.Contains("limit is 200", Validate(Create(description: new string('a', 201))));

    [Fact]
    public void BuildMarkdown_RoundTripsThroughNormalize()
    {
        var draft = Create(description: "How to write release notes (short), for this repo; see /docs.");
        Assert.Null(Validate(draft));
        var markdown = FleetSkillProposals.BuildMarkdown(draft.SkillName, draft.Description!, draft.Replacement);

        var staging = Path.Combine(Path.GetTempPath(), "skill-proposal-test-" + Guid.NewGuid().ToString("n"));
        try
        {
            var result = SkillPackage.Normalize("SKILL.md", System.Text.Encoding.UTF8.GetBytes(markdown), staging);
            Assert.Null(result.Error);
            var skill = Assert.Single(result.Skills);
            Assert.Equal(draft.SkillName, skill.Name);
            Assert.Equal(draft.Description, skill.Description);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public void Validate_Create_LookalikeBaseline()
    {
        // A Cyrillic 'а' that occurs nowhere in the user's skills is refused; one the user's own skill already uses is not.
        Assert.Contains("not Latin", Validate(Create(body: "Group by аrea.")));
        var withCyrillic = Installed(new FleetSkillSnapshot("ru", "Russian notes.", Md("ru", "Привет а")));
        Assert.Null(Validate(Create(body: "Group by аrea."), withCyrillic));
    }

    [Fact]
    public void Validate_AllowsShellAndToolGuidance()
    {
        // ADR 0146: no gated-term rule for skills. The user's Approve is the control.
        Assert.Null(Validate(Edit(kind: FleetSkillProposalKind.Append, find: null,
            replacement: "Run the build in the shell with dotnet build, then call the delegate_to_team tool and wait for approval.")));
        Assert.Null(Validate(Create(body: "Use the Copilot shell to run the scripts, with model: any tools: all.")));
    }

    [Fact]
    public void Apply_OnlyEditsTheBody()
    {
        // A passage that also occurs in the frontmatter is matched in the BODY only, and the frontmatter comes back byte for byte.
        var markdown = FleetSkillProposals.BuildMarkdown("deploys", "Run the build.", "Run the build.\nDone.");
        var applied = FleetSkillProposals.Apply(markdown, Stored("p", find: "Run the build.", replacement: "Run the build twice."));

        Assert.True(applied.Ok);
        Assert.StartsWith("---\nname: deploys\ndescription: Run the build.\n---\n", applied.Markdown);
        Assert.Contains("Run the build twice.", applied.Markdown);
        Assert.Equal(markdown[..markdown.IndexOf("Run the build.\nDone", StringComparison.Ordinal)], applied.Markdown![..markdown.IndexOf("Run the build.\nDone", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Apply_ReappliesOnCurrentText_Outdated_AppendIdempotent()
    {
        var current = Md();
        var replace = Stored("p");
        var first = FleetSkillProposals.Apply(current, replace);
        Assert.True(first.Ok);
        Assert.Contains("and the linter", first.Markdown);

        // The passage is gone: outdated. Two copies: also refused.
        Assert.False(FleetSkillProposals.Apply(first.Markdown!, replace).Ok);
        Assert.Contains("2 times", FleetSkillProposals.Apply(Md(body: "A.\nA."), Stored("p", find: "A.")).Problem);

        var append = Stored("a", kind: FleetSkillProposalKind.Append, find: null, replacement: "Post the build link.");
        var appended = FleetSkillProposals.Apply(current, append);
        Assert.True(appended.Ok);
        Assert.False(appended.AlreadyApplied);
        Assert.True(FleetSkillProposals.Apply(appended.Markdown!, append).AlreadyApplied);

        // CRLF on disk is compared as LF.
        Assert.True(FleetSkillProposals.Apply(current.Replace("\n", "\r\n"), replace).Ok);
        Assert.False(FleetSkillProposals.Apply("no frontmatter here", replace).Ok);
    }

    [Fact]
    public void Merge_Caps_Tombstones_Expiry()
    {
        var document = new FleetLessonsDocument
        {
            SkillProposals =
            [
                Stored("old", "stale", created: Now.AddDays(-(Options.LessonTtlDays + 1))),
                Stored("dismissed", "nope", status: FleetPromptProposalStatus.Dismissed, find: "x", replacement: "y"),
            ],
        };
        var drafts = new[]
        {
            Edit("nope", find: "x", replacement: "y"), // a repeat of the dismissed one
            Edit("deploys"),
            Edit("deploys", find: "other", replacement: "other2"), // a second for the same skill
            Create("one"),
            Create("two"), // over the per-run cap of 2
        };

        var ids = new Queue<string>(["n1", "n2", "n3"]);
        var merged = FleetSkillProposals.Merge(document, drafts, Now, Options, ids.Dequeue);

        Assert.Equal(2, merged.Proposed);
        Assert.Equal(3, merged.Refused);
        Assert.DoesNotContain(merged.Document.SkillProposals, p => p.Id == "old");
        Assert.Contains(merged.Document.SkillProposals, p => p.Id == "dismissed");
        Assert.Equal(["deploys", "one"], merged.Document.SkillProposals.Where(p => p.Status == FleetPromptProposalStatus.Pending).Select(p => p.SkillName));
        Assert.Equal("How to write release notes for this repo.", merged.Document.SkillProposals.Single(p => p.SkillName == "one").Description);
        Assert.Null(merged.Document.SkillProposals.Single(p => p.SkillName == "deploys").Description);

        // Five pending is the ceiling.
        var full = new FleetLessonsDocument { SkillProposals = [.. Enumerable.Range(0, 5).Select(i => Stored($"f{i}", $"s{i}"))] };
        Assert.Equal(0, FleetSkillProposals.Merge(full, [Create("six")], Now, Options).Proposed);

        // Decided proposals are capped at 50, oldest decision first.
        var decided = new FleetLessonsDocument
        {
            SkillProposals = [.. Enumerable.Range(0, 55).Select(i => Stored($"d{i}", $"s{i}", FleetPromptProposalStatus.Approved, decided: Now.AddMinutes(i)))],
        };
        var expired = FleetSkillProposals.Expire(decided, Now, Options);
        Assert.Equal(FleetSkillProposals.MaxDecided, expired.SkillProposals.Count);
        Assert.DoesNotContain(expired.SkillProposals, p => p.Id == "d0");
    }
}
