namespace Agent42.Tests;

/// <summary>
/// Serialises the test classes that build a live-telemetry capture host, because the thing they capture
/// from is <b>process-global</b>.
///
/// <para><c>AddLiveTelemetryCapture</c> subscribes a <c>LiveTelemetryBuffer</c> to the
/// <see cref="LiveTelemetryCaptureExtensions.OtelSourceName"/> <c>ActivitySource</c>, which is a
/// process-wide name, not a per-host one. So while one test's host is alive, a span emitted anywhere
/// else in the process on that same source lands in <em>its</em> buffer. xUnit runs test classes in
/// parallel by default, and two of these classes sit on opposite sides of that: <c>MeterNamingTests</c>
/// drives a real instrumented chat call through
/// <c>UseOpenTelemetry(sourceName: OtelSourceName)</c> — which emits a span — while
/// <c>OtelConfigurationTests</c> asserts its freshly-built buffer is still empty.</para>
///
/// <para>The result was an intermittent <c>Assert.Empty() Failure: Collection was not empty</c>,
/// reproduced roughly twice in six runs under <c>--collect:"XPlat Code Coverage"</c> and observed
/// failing the <c>new-code-coverage</c> PR check. It is a race between the two classes, not a defect in
/// either, and it predates the branch that surfaced it (four consecutive coverage runs on an unmodified
/// <c>main</c> worktree were green — this is a timing-sensitive latent flake, not a regression).</para>
///
/// <para>Sharing one collection is what fixes it: xUnit never runs two classes of the same collection
/// concurrently, so no assertion had to be weakened to make it deterministic. This is the
/// <c>StaticAssets</c> idiom, minus a fixture — there is no shared state to hand out, only an ordering
/// constraint. <b>Any new test class that builds a live-capture host, or that emits on
/// <c>OtelSourceName</c>, belongs in this collection too.</b></para>
/// </summary>
[CollectionDefinition("LiveTelemetry")]
public sealed class LiveTelemetryCollection;
