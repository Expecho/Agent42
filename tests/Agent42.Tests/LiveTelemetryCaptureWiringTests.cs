namespace Agent42.Tests;

/// <summary>
/// Both hosts must switch the live telemetry console on (ADR 0054) — and they do it with <b>different</b>
/// arguments, which is the part worth pinning.
///
/// <para>This is a <b>source-text</b> guard in the <c>*AssetsTests</c> style, because nothing in this suite
/// runs either <c>Program.cs</c>: the Backend's needs Cosmos, blob storage and an Orleans silo, and
/// DevHost's builds a whole second web host. So the assertions read the two files off disk. Crude, but the
/// alternative is no coverage at all of a call whose absence is silent — capture that is never registered
/// produces an empty console, not an error.</para>
/// </summary>
public sealed class LiveTelemetryCaptureWiringTests
{
    private static string ReadHost(params string[] relativePath) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. relativePath]));

    [Fact]
    public void BothHosts_RegisterLiveTelemetryCapture()
    {
        var backend = ReadHost("src", "Agent42.Backend", "Program.cs");
        var devHost = ReadHost("tools", "Agent42.DevHost", "Program.cs");

        Assert.Contains("AddLiveTelemetryCapture(", backend, StringComparison.Ordinal);
        Assert.Contains("AddLiveTelemetryCapture(", devHost, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyDevHost_AsksCaptureToAddInstrumentation()
    {
        var backend = ReadHost("src", "Agent42.Backend", "Program.cs");
        var devHost = ReadHost("tools", "Agent42.DevHost", "Program.cs");

        // DevHost deliberately does NOT call AddServiceDefaults() — that would drag in resilience
        // handlers, health checks and service discovery it does without — so it has no OpenTelemetry
        // pipeline at all and capture has to bring the sources and instrumentation with it.
        Assert.Contains("AddLiveTelemetryCapture(addInstrumentation: true)", devHost, StringComparison.Ordinal);

        // The Backend must NOT: AddServiceDefaults() has already registered AspNetCore/Http/Runtime
        // instrumentation there, and registering it twice risks duplicate spans. The logging provider's
        // additivity was measured; instrumentation additivity is deliberately not assumed.
        Assert.DoesNotContain("AddLiveTelemetryCapture(addInstrumentation: true)", backend, StringComparison.Ordinal);
        Assert.Contains("AddLiveTelemetryCapture();", backend, StringComparison.Ordinal);
    }

    [Fact]
    public void DevHost_StillRegistersNoOtlpExporter()
    {
        var devHost = ReadHost("tools", "Agent42.DevHost", "Program.cs");

        // Offline capture exists so the feature is exercisable without Azure, not so telemetry leaves the
        // machine. The buffer behind /logs is the only sink here.
        //
        // Matched as a CALL ("builder.X(") rather than as a bare name: DevHost's own comment explains why
        // it does not call AddServiceDefaults, and the looser check fails on that explanation.
        Assert.DoesNotContain("AddOtlpExporter(", devHost, StringComparison.Ordinal);
        Assert.DoesNotContain("builder.AddServiceDefaults(", devHost, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
