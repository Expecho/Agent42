using System.Diagnostics;

namespace Agent42.Tests;

/// <summary>
/// Guards <c>.claude/hooks/protect-main.mjs</c> — the agent-side half of CLAUDE.md's first hard rule,
/// never commit or push to a protected branch.
///
/// <para>The hook used to read one branch, this repository's, from <c>CLAUDE_PROJECT_DIR</c>. That is
/// wrong in both directions once a command names another checkout, and the two halves are not equally
/// visible. The false positive announces itself: a commit to a feature branch in a sibling repository
/// is refused because Agent42 happens to sit on main, which is how this was found. The mirror image
/// is silent and is the one that matters — a commit landing on main HERE, judged against a sibling's
/// feature branch and waved through.</para>
///
/// <para>These invoke the real hook as a subprocess against throwaway checkouts rather than
/// re-implementing its parsing, because a reimplementation would pass while the shipped hook
/// regressed — the failure mode this file exists to catch.</para>
///
/// <para>A note for whoever edits this file next: the hook matches COMMAND TEXT, so a Bash heredoc
/// writing these fixtures trips it on its own contents. Edit this file with a file-writing tool, not
/// by echoing it from a shell.</para>
/// </summary>
public sealed class ProtectMainHookTests : IDisposable
{
    private readonly string _temp;
    private readonly string _root;
    private readonly string _siblingOnFeature;
    private readonly string _siblingOnMain;

    public ProtectMainHookTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "a42-hook-" + Guid.NewGuid().ToString("N"));
        _root = InitRepo("root", "main");
        _siblingOnFeature = InitRepo("sibling-feature", "feature/x");
        _siblingOnMain = InitRepo("sibling-main", "main");

        // The hook reads its protected list from CLAUDE_PROJECT_DIR, so the fixture root carries its
        // own copy of both the script and the list. Writing the list rather than leaning on the
        // built-in fallback keeps the test honest about which one it actually read.
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "hooks"));
        Directory.CreateDirectory(Path.Combine(_root, ".agentic"));
        File.Copy(
            Path.Combine(RepoRoot(), ".claude", "hooks", "protect-main.mjs"),
            Path.Combine(_root, ".claude", "hooks", "protect-main.mjs"));
        File.WriteAllText(
            Path.Combine(_root, ".agentic", "quality-gates.yaml"),
            "branches:\n  protected: [main]\n");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Agent42.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>
    /// A checkout whose only interesting property is the branch it is on. No commit is made:
    /// <c>git branch --show-current</c> reports an unborn branch by name, which is all the hook reads.
    /// </summary>
    private string InitRepo(string name, string branch)
    {
        var dir = Path.Combine(_temp, name);
        Directory.CreateDirectory(dir);
        Run("git", $"init -b {branch}", dir);
        return dir;
    }

    private static void Run(string file, string args, string cwd)
    {
        using var proc = Process.Start(new ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }

    /// <summary>Feeds the hook a PreToolUse payload and reports whether it blocked (exit 2).</summary>
    private (int ExitCode, string Output) RunHook(string command)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            tool_name = "Bash",
            tool_input = new { command },
        });

        var psi = new ProcessStartInfo("node", Path.Combine(_root, ".claude", "hooks", "protect-main.mjs"))
        {
            WorkingDirectory = _temp,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["CLAUDE_PROJECT_DIR"] = _root;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(payload);
        proc.StandardInput.Close();
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    // The regression. Sibling repository, feature branch, and this repository on main — the exact
    // shape that blocked a legitimate commit to another checkout mid-session.
    [Fact]
    public void ACommitTargetingASiblingOnAFeatureBranch_IsAllowed()
    {
        var (exit, _) = RunHook($"git -C {_siblingOnFeature} commit -m x");

        Assert.Equal(0, exit);
    }

    // The silent half, and the reason the fix collects candidates instead of just swapping which
    // directory is read. A sibling on a feature branch must not launder a commit onto main here.
    [Fact]
    public void ACommitHere_OnMain_IsStillBlocked_EvenBesideASiblingOnAFeatureBranch()
    {
        var (exit, output) = RunHook($"git -C {_siblingOnFeature} commit -m x && git commit -m y");

        Assert.Equal(2, exit);
        Assert.Contains("BLOCKED", output, StringComparison.Ordinal);
    }

    // The gate still WORKS in its ordinary case. Without this, every assertion here could pass
    // against a hook that had stopped blocking anything at all.
    [Fact]
    public void APlainCommitFromTheSessionRootOnMain_IsBlocked()
    {
        var (exit, output) = RunHook("git commit -m x");

        Assert.Equal(2, exit);
        Assert.Contains("BLOCKED", output, StringComparison.Ordinal);
    }

    // Protection follows the repository, not this one. A sibling that is itself on main is refused
    // exactly as this repository would be — the fix must not become "anything elsewhere is fine".
    [Fact]
    public void ACommitTargetingASiblingOnMain_IsBlocked()
    {
        var (exit, output) = RunHook($"git -C {_siblingOnMain} commit -m x");

        Assert.Equal(2, exit);
        Assert.Contains("BLOCKED", output, StringComparison.Ordinal);
    }

    // The other way a command names a repository. `cd <path> && git ...` is the form the agent reaches
    // for most, and it carries no -C to parse.
    [Fact]
    public void ALeadingCd_IntoASiblingOnAFeatureBranch_IsAllowed()
    {
        var (exit, _) = RunHook($"cd {_siblingOnFeature} && git commit -m x");

        Assert.Equal(0, exit);
    }

    [Fact]
    public void ALeadingCd_IntoASiblingOnMain_IsBlocked()
    {
        var (exit, output) = RunHook($"cd {_siblingOnMain} && git commit -m x");

        Assert.Equal(2, exit);
        Assert.Contains("BLOCKED", output, StringComparison.Ordinal);
    }

    // Pushing a feature branch of another checkout is the natural sequel to committing there.
    [Fact]
    public void APushOfASiblingsFeatureBranch_IsAllowed()
    {
        var (exit, _) = RunHook($"git -C {_siblingOnFeature} push -u origin feature/x");

        Assert.Equal(0, exit);
    }

    // The explicit-ref guard reads the command text alone and is deliberately untouched by any of
    // this: naming a protected ref is refused wherever the repository sits.
    [Fact]
    public void APushNamingAProtectedRef_IsBlocked_EvenFromASiblingOnAFeatureBranch()
    {
        var (exit, output) = RunHook($"git -C {_siblingOnFeature} push origin main");

        Assert.Equal(2, exit);
        Assert.Contains("BLOCKED", output, StringComparison.Ordinal);
    }

    // Nothing git-shaped, nothing to say. Guards the early exit that keeps the hook off every other
    // Bash call in a session.
    [Fact]
    public void ACommandThatIsNotACommitOrPush_IsAllowed()
    {
        var (exit, _) = RunHook("git status --short");

        Assert.Equal(0, exit);
    }
}
