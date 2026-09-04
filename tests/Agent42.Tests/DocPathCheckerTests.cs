using System.Diagnostics;

namespace Agent42.Tests;

/// <summary>
/// Guards <c>.agentic/scripts/check-doc-paths.mjs</c> — the CI hard block that keeps agent docs from
/// citing paths that no longer exist.
///
/// <para>ADR 0063 gave that script its first ability to <b>switch itself off</b> for a file: a totally
/// superseded ADR is skipped, because paths naming since-deleted code are accurate history there and
/// editing them would both corrupt the record and breach <c>decisions/README.md</c>'s "never edit an
/// accepted ADR's decision content". Everything about that is fail-open by construction, which is why
/// it is worth a test: the two properties below are the whole safety argument, and until now both were
/// held by a code comment and ADR prose alone.</para>
///
/// <para>These invoke the real script as a subprocess against fixture directories rather than
/// re-implementing its parsing, because a reimplementation would pass while the shipped script
/// regressed — the failure mode this file exists to catch.</para>
/// </summary>
public sealed class DocPathCheckerTests
{
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
    /// Runs the real checker with <paramref name="adrBody"/> as the only ADR in a throwaway repo, and
    /// reports whether it flagged the bogus path inside. The script scans relative to its working
    /// directory, so the fixture is a directory tree, not a stubbed filesystem.
    /// </summary>
    private static (int ExitCode, string Output) RunAgainst(string adrBody)
    {
        var temp = Path.Combine(Path.GetTempPath(), "a42-docpaths-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, ".agentic", "decisions"));
            Directory.CreateDirectory(Path.Combine(temp, ".agentic", "scripts"));
            File.Copy(
                Path.Combine(RepoRoot(), ".agentic", "scripts", "check-doc-paths.mjs"),
                Path.Combine(temp, ".agentic", "scripts", "check-doc-paths.mjs"));
            File.WriteAllText(Path.Combine(temp, ".agentic", "decisions", "0001-fixture.md"), adrBody);

            var psi = new ProcessStartInfo("node", ".agentic/scripts/check-doc-paths.mjs")
            {
                WorkingDirectory = temp,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, output);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private const string BogusPath = "`src/Agent42.Agents/NoSuchFolder/NoSuchFile.cs`";

    // The gate still WORKS. Without this, every assertion below could pass against a script that
    // skipped everything, which is the one way this file could be worse than no file at all.
    [Fact]
    public void AnOrdinaryAdr_CitingAMissingPath_StillFails()
    {
        var (exit, output) = RunAgainst(
            $"---\nid: 0001\nstatus: accepted\n---\n\n- **Evidence:** {BogusPath}\n");

        Assert.Equal(1, exit);
        Assert.Contains("NoSuchFile.cs", output, StringComparison.Ordinal);
    }

    // ADR 0063's actual decision: a TOTALLY superseded ADR is history, so its dead paths are accurate
    // and are skipped.
    [Fact]
    public void ATotallySupersededAdr_IsSkipped()
    {
        var (exit, output) = RunAgainst(
            $"---\nid: 0001\nstatus: superseded\nsuperseded_by: 0002\n---\n\n- **Evidence:** {BogusPath}\n");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("NoSuchFile.cs", output, StringComparison.Ordinal);
    }

    // The load-bearing half of the key choice, and the reason it is `status:` and NOT `superseded_by:`.
    // ADR 0007 carries `superseded_by: 0008  # partial` while staying `status: accepted` and
    // authoritative; keying on the tag would have silently stopped checking it. If this test ever goes
    // green by being deleted, the gate has quietly lost every partially superseded ADR.
    [Fact]
    public void APARTIALLYSupersededAdr_IsStillChecked()
    {
        var (exit, output) = RunAgainst(
            $"---\nid: 0001\nstatus: accepted\nsuperseded_by: 0002  # partial\n---\n\n- **Evidence:** {BogusPath}\n");

        Assert.Equal(1, exit);
        Assert.Contains("NoSuchFile.cs", output, StringComparison.Ordinal);
    }

    // The skip is scoped to the front-matter block, not the whole file. `.agentic/decisions/README.md`
    // is itself scanned AND documents this exact key, so a body mention — a fenced example, a quoted
    // rule, a reflow — must not switch path checking off for the file. This fails open when wrong,
    // which is why it is asserted rather than reasoned about.
    [Fact]
    public void TheStatusTag_InTheBODY_DoesNotDisableChecking()
    {
        var (exit, output) = RunAgainst(
            "---\nid: 0001\nstatus: accepted\n---\n\n"
                + "Superseding sets both tags:\n\n```\nstatus: superseded\n```\n\n"
                + $"- **Evidence:** {BogusPath}\n");

        Assert.Equal(1, exit);
        Assert.Contains("NoSuchFile.cs", output, StringComparison.Ordinal);
    }
}
