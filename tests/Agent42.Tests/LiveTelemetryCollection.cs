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
///
/// <para><b>⚠ Correction, 2026-09-03 (ADR 0062).</b> The diagnosis above is real but <b>incomplete</b>,
/// and acting on it alone leaves a residual flake that looks identical. Four more classes were added to
/// this collection during the Orleans 10.3.1 bump — <c>LiveMetricExporterTests</c>,
/// <c>LiveSpanProcessorTests</c>, <c>LiveTelemetryBufferTests</c>,
/// <c>LiveTelemetryCaptureWiringTests</c> — which reduced the failure rate but did not remove it.
/// Dumping the buffer on a failing run showed the contaminants were <b>not gen_ai spans</b> at all:
/// they were <c>Source = Microsoft.AspNetCore</c> server spans
/// (<c>PUT /api/conversations/{conversationId}/pin</c>, <c>GET /api/conversations</c>) from other
/// classes' <c>TestServer</c> fixtures.</para>
///
/// <para>The mechanism is one level up from <c>AddSource</c>: <c>ConfigureOpenTelemetry()</c>
/// subscribes to the <b>ASP.NET Core instrumentation</b>, and an <c>ActivityListener</c> is
/// process-global, so while any such host is alive it captures every server span in the process. The
/// contaminators are therefore <b>the entire API test suite</b>, not three classes — so no collection
/// membership can ever fix it, and adding more classes here is the wrong lever. An assertion that a
/// live-capture buffer is <em>empty</em> must be scoped to the source under test
/// (<c>OtelConfigurationTests.AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline</c> shows the
/// shape). It reproduces far more readily under <c>--collect:"XPlat Code Coverage"</c>, which is the CI
/// path, because the instrumented host build holds the listener open for seconds rather than
/// milliseconds.</para>
/// </summary>
[CollectionDefinition("LiveTelemetry")]
public sealed class LiveTelemetryCollection;
