# Plan — Enrich telemetry with OpenTelemetry `user.*` attributes

planned-against: main @ ab1b0ab

## Overview

This is a multi-user system (two Entra users today, behind Easy Auth) whose traces
currently carry **no user attribution** — you cannot tell whose request produced a span.
Goal: stamp the OpenTelemetry semantic-convention `user.*` attributes onto the ASP.NET Core
**server span** of every authenticated request, capturing as much identity as Easy Auth
exposes (id, name, email, full name, roles) plus the identity provider. Traces only — logs
are explicitly out of scope this round (see Out of scope).

This is a privacy-relevant change: user PII (name, email, roles) will flow to **both** OTLP
collectors the app already exports to (the env-var endpoint and the config-driven second
collector, `home.peterbons.nl:4317`). The user explicitly chose "store as much as possible";
that decision and its alternatives are recorded as an ADR in this plan (Step 4).

## Affected areas

- `src/Agent42.Backend/CurrentUser.cs` — extend with a pure, testable attribute extractor
  (today it exposes only `GetUserId`). This is the single home of Easy Auth header knowledge.
- `src/Agent42.Backend/Program.cs` — wire the enrichment callback onto the ASP.NET Core
  trace instrumentation (lines 13–18 area, right after `AddServiceDefaults()`).
- `src/Agent42.Backend/Agent42.Backend.csproj` — add a direct `PackageReference` to
  `OpenTelemetry.Instrumentation.AspNetCore` (currently only transitive via ServiceDefaults)
  so `AspNetCoreTraceInstrumentationOptions` is unambiguously in scope. Version is centrally
  pinned in `Directory.Packages.props` (1.15.2) — **do not** write a version in the csproj.
- `tests/Agent42.Tests/` — new `UserTelemetryTests.cs` (unit + one wiring test).
- `.agentic/decisions/` — new ADR (PII-in-telemetry decision); minor wording fix to
  `0002-entra-easy-auth-gate.md`.
- Docs sweep (`sync-docs`): CLAUDE.md / gotchas as the diff dictates.

## Context & constraints

- **No custom instrumentation exists.** Grep for `ActivitySource`/`StartActivity`/`SetTag`/
  `IHttpContextAccessor` across `src` returns nothing but a `AddSource("Agent42.Backend")`
  registration (`Program.cs:11,17-18`) that is never used to start an activity. Telemetry is
  purely auto-instrumentation: ASP.NET Core **server** spans, HttpClient **client** spans,
  runtime metrics (`src/Agent42.ServiceDefaults/Extensions.cs:51-83`). **The only enrichable
  span for an incoming request is the ASP.NET Core server span** — that is the target.

- **Enrichment approach (primary): `EnrichWithHttpRequest`.** Configure
  `AspNetCoreTraceInstrumentationOptions.EnrichWithHttpRequest` from Backend `Program.cs`
  (via `builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(...)`), so the
  Easy-Auth-specific logic stays in the Backend and **ServiceDefaults stays generic** (it is
  a shared Aspire project; ADR 0002's separation of concerns keeps Easy Auth knowledge in
  `CurrentUser.cs`). The `AddAspNetCoreInstrumentation(t => t.Filter = ...)` call already in
  ServiceDefaults (`Extensions.cs:69-74`) sets a *different* property, so a second `Configure`
  delegate composes with it (both run; no overwrite). In 1.15.2 the signature is
  `Action<Activity, HttpRequest>` — **confirm at build time** (Step 2 runs `dotnet build`).
- **Enrichment approach (documented fallback).** If the options type/property is not reachable
  or composition misbehaves, use an inline `app.Use(async (ctx, next) => { … Activity.Current
  … await next(); })` middleware registered first in the pipeline — the server Activity is
  started by the instrumentation *before* user middleware, so `Activity.Current` in the first
  middleware **is** the server span, and tags set there export identically. Same extractor,
  same tests. Pick one; do not ship both.

- **Keep the extractor pure and header-shaped, and make the Activity-application testable.**
  Add to `CurrentUser.cs`:
  - `public static IEnumerable<KeyValuePair<string, object>> GetUserTelemetryTags(this IHeaderDictionary headers)`
    — the pure extractor. Operating on `IHeaderDictionary` (not `HttpContext`) makes it trivially
    unit-testable with a plain `HeaderDictionary`; add a one-line rationale comment so the next
    reader doesn't "fix" it back to the file's `HttpContext`-receiver shape (M3).
  - `public static void ApplyUserTelemetryTags(this Activity activity, IHeaderDictionary headers)`
    — `foreach (var tag in headers.GetUserTelemetryTags()) activity.SetTag(tag.Key, tag.Value);`.
    This is the exact code the enrichment callback runs, so a test can call it on a real
    `Activity` and prove tags (incl. the `user.roles` **array**) actually land via `SetTag` —
    the wiring proof that AC6 needs **without** booting a host (see Step 3 / B1 fix).

  The enrichment callback then reduces to `activity.ApplyUserTelemetryTags(request.Headers)`.
  **Do not touch `GetUserId`'s public behaviour** — it keys every grain (`AgentApi.cs`, 15 call
  sites) and must keep its exact `local-dev` fallback semantics. To avoid duplicating the
  principal-id read + `local-dev` fallback across the two receivers (`duplication_pct_max: 3`,
  M2), factor the id resolution into **one private helper over `IHeaderDictionary`** that both
  `GetUserId` and `GetUserTelemetryTags` call, reusing the existing `LocalDevelopmentUserId`
  const — so the two can never drift on the fallback semantics this plan wants preserved.

- **Attribute → source mapping.** Easy Auth injects these (ingress strips inbound
  `X-MS-CLIENT-*`, so they are trustworthy — ADR 0002):
  | OTel attribute | semconv? | Source |
  |---|---|---|
  | `user.id` | yes | `X-MS-CLIENT-PRINCIPAL-ID` header (oid); `local-dev` fallback |
  | `user.name` | yes | `X-MS-CLIENT-PRINCIPAL-NAME` header (UPN/display name) |
  | `user.email` | yes | claims blob — `preferred_username`/`upn`/`email`/`emailaddress` claim |
  | `user.full_name` | yes | claims blob — short `name` / `.../claims/name` claim |
  | `user.roles` | yes | claims blob — **all** `roles` / `.../claims/role` claims → `string[]` |
  | `user.idp` | **no (custom)** | `X-MS-CLIENT-PRINCIPAL-IDP` header. Non-standard key — documented deviation |
  Emit a tag only when its value is present/non-empty. `user.roles` must be set as a
  **multi-value (array)** attribute (`SetTag` accepts `string[]`; OTLP exports it as an array).

- **The claims blob is `X-MS-CLIENT-PRINCIPAL`: standard base64 of JSON**
  `{ "auth_typ", "name_typ", "role_typ", "claims": [ { "typ", "val" }, … ] }`. Decode with
  `Convert.FromBase64String` → UTF-8 → `JsonDocument`; iterate `claims`, matching `typ` against
  the **candidate lists** above (claim-type spelling varies between AAD v1/v2 and Easy Auth's
  mapping). Parsing MUST be defensive: a missing, non-base64, or non-JSON blob must **not
  throw** — fall back to the header-only attributes. **This blob's exact claim `typ` spellings
  are a prod-only fact** (Easy Auth is absent locally — ADR 0002; DevHost doesn't even call
  `AddServiceDefaults`), so end-to-end shape **cannot be verified offline**; unit tests use a
  synthetic blob built from the candidate `typ` values, and the real mapping is a prod-smoke
  check. If a claim type turns out wrong in prod, that is a `/gotcha` candidate, not a code bug.

- **PII trade-off & alternatives (for the ADR).** Chosen: full set above. Rejected in the
  interview: `user.id`-only (attribution by oid, names resolved externally); id+name/email;
  adding `user.hash` (a stable non-PII hash) alongside. No config kill-switch is added
  (YAGNI for a two-user personal app) — noted as out of scope.

- **ADR 0002 drift.** Its Decision paragraph says "The app itself only reads
  `X-MS-CLIENT-PRINCIPAL-ID` (`CurrentUser.cs`)". After this change the app also reads
  `-NAME`, `-IDP`, and the `X-MS-CLIENT-PRINCIPAL` blob. Fix that sentence in the same MR
  (hard rule 2) — a one-line clarification pointing at the new ADR.

## Steps

1. **Extend `CurrentUser.cs` with the telemetry extractor + Activity applier.** Add
   `GetUserTelemetryTags(this IHeaderDictionary)` (returns the `user.*` + `user.idp` tags per the
   mapping table, with defensive base64/JSON claim parsing) and
   `ApplyUserTelemetryTags(this Activity, IHeaderDictionary)` (the `SetTag` loop). Factor the
   principal-id read + `local-dev` fallback into one private `IHeaderDictionary` helper that
   `GetUserId` now also calls, so `GetUserId`'s public behaviour is unchanged but the fallback
   lives in one place (M2). Extract header-name and claim-type constants.
   *Verify:* `dotnet build Agent42.slnx`; unit tests in Step 3 are the behavioural proof.
   *Files:* `src/Agent42.Backend/CurrentUser.cs`.

2. **Wire the enrichment + package reference.** Add the direct `PackageReference`
   (no version) to `Agent42.Backend.csproj`; in `Program.cs`, after `AddServiceDefaults()`,
   register `builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
   o.EnrichWithHttpRequest = (activity, request) => activity.ApplyUserTelemetryTags(request.Headers))`
   with the needed `using`. (Fallback middleware only if the build rejects the type — see Context.)
   *Verify:* `dotnet build Agent42.slnx` compiles and confirms the `EnrichWithHttpRequest`
   signature; `dotnet run --project tools/Agent42.DevHost` still boots (DevHost has no OTel,
   so this only proves no startup regression).
   *Files:* `src/Agent42.Backend/Program.cs`, `src/Agent42.Backend/Agent42.Backend.csproj`.
   *Depends on Step 1.*

3. **Tests — `tests/Agent42.Tests/UserTelemetryTests.cs`.** All deterministic, no host boot.
   NOTE for the executor: the repo has **no `WebApplicationFactory` / `Mvc.Testing`**; the only
   HTTP fixture (`AgentApiTests`) hand-builds a slim app via `WebApplication.CreateSlimBuilder()`
   + `MapAgentApi()` and **never runs `Program.cs` or configures OTel** (`AgentApiTests.cs:32-46`).
   So the enrichment can't be proven by booting a server, and a bare `ActivityListener` does not
   invoke `EnrichWithHttpRequest` (only OTel's AspNetCore instrumentation does). Prove it instead
   at the helper level:
   - **Extractor unit tests** (`GetUserTelemetryTags` on hand-built `HeaderDictionary`):
     (a) full — id+name+idp headers + synthetic base64 blob with email/name/multiple-roles
     claims → all six tags present, `user.roles` is an array; (b) id-only header → only
     `user.id`, no throw; (c) no headers → `user.id == "local-dev"`, no PII tags; (d)
     malformed/non-base64 blob → no throw, header attrs still emitted.
   - **Activity-application test** (the wiring proof, AC6): create a real `Activity`
     (`new Activity("test").Start()` — a directly-constructed Activity records tags with **no
     listener needed**), call `ApplyUserTelemetryTags(headers)` with the full Easy Auth header
     set, and assert via `activity.GetTagItem("user.id")` … that every attribute landed,
     including `user.roles` as a `string[]`. This exercises the exact `SetTag` code the
     production callback runs — the untested residue is only the single `Program.cs` line
     assigning the delegate + OTel invoking it, which is prod-smoke (AC9), not offline-testable.
   *Verify:* `dotnet test Agent42.slnx`.
   *Files:* `tests/Agent42.Tests/UserTelemetryTests.cs`. *Depends on Steps 1–2.*

4. **Record the decision (`/decide`) + fix ADR 0002.** New ADR: "Enrich telemetry with user
   identity attributes" — context (multi-user, no attribution), decision (full `user.*` set to
   both OTLP collectors), alternatives (id-only / +email / +`user.hash` / config kill-switch),
   consequences (PII leaves the app to two collectors; `user.idp` is a non-semconv custom key).
   Then update the stale "only reads `X-MS-CLIENT-PRINCIPAL-ID`" sentence in
   `0002-entra-easy-auth-gate.md`. *Independent of Steps 1–3 (parallelizable), but the ADR must
   land in the same MR.*
   *Verify:* doc-path integrity (CI) / `/audit-docs` spot check.

5. **Docs sweep (`/sync-docs`).** Sweep the diff: does CLAUDE.md's telemetry/auth note need a
   line? Is there a durable, code-invisible trap worth a `/gotcha` (the prod-only claim-`typ`
   mapping is the prime candidate)? Fix in this MR. *Depends on Steps 1–4.*
   *Verify:* `dotnet format Agent42.slnx --verify-no-changes`; `/audit-docs` if docs changed.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proving test | File |
|---|----------------------|--------------|------|
| AC1 | Full Easy Auth identity → server span carries `user.id`, `user.name`, `user.email`, `user.full_name`, `user.roles`, `user.idp` | `GetUserTelemetryTags_FullPrincipal_EmitsAllAttributes` | `UserTelemetryTests.cs` |
| AC2 | `user.roles` is emitted as a multi-value (array) attribute | `GetUserTelemetryTags_MultipleRoleClaims_EmitsArray` | `UserTelemetryTests.cs` |
| AC3 | Id-only header (no blob) → `user.id` set, other attrs simply absent, no throw | `GetUserTelemetryTags_IdOnly_EmitsIdOnly` | `UserTelemetryTests.cs` |
| AC4 | No Easy Auth headers → `user.id == "local-dev"`, no PII, no throw | `GetUserTelemetryTags_NoHeaders_FallsBackToLocalDev` | `UserTelemetryTests.cs` |
| AC5 | Malformed/non-base64 `X-MS-CLIENT-PRINCIPAL` → no throw, header attrs still emitted | `GetUserTelemetryTags_MalformedPrincipal_DoesNotThrow` | `UserTelemetryTests.cs` |
| AC6 | `ApplyUserTelemetryTags` stamps `user.*` (incl. `user.roles` array) onto a live `Activity` via `SetTag` — the exact code the callback runs | `ApplyUserTelemetryTags_FullPrincipal_StampsActivity` (real `Activity` + `GetTagItem`) | `UserTelemetryTests.cs` |
| AC7 | No telemetry regression: dual-exporter pipeline still builds; `/health`,`/alive` still filtered | existing `OtelConfigurationTests` (unchanged, must stay green) | `OtelConfigurationTests.cs` |
| AC8 | `GetUserId` grain-keying unchanged | existing `AgentApiTests` (unchanged, must stay green) | `AgentApiTests.cs` |
| AC9 | OTel actually invokes the callback on the real server span in prod | **prod-smoke only** — verify `user.*` on a trace at the collector after deploy; not offline-testable (Easy Auth + OTel are prod-only) | — |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80` (extractor is heavily unit-tested; the ~2 wiring lines are
  covered by AC6)
- `cognitive_complexity_max: 15` (watch the claim-parsing method — keep the `typ`-matching in a
  small helper if it approaches the limit)
- `dependencies.vulnerable_packages: 0`; new dep resolved via central pin, not from memory
- Branch: `feature/otel-user-attributes` (never commit to `main` — hard rule 1); MR required.

## Out of scope

- **Logs.** No request-scoped logger scope this round (user chose traces only), even though
  `IncludeScopes = true` would make it cheap — a fast follow if wanted.
- **Outbound/grain spans.** The Azure OpenAI HttpClient client spans and Orleans grain/reminder
  activities run off the request's execution context (and reminders have no `HttpContext` at
  all), so they are **not** enriched here. Grain-side telemetry is attributable via the grain key
  (which already embeds the user id). Baggage propagation to cover them is a separate effort.
- **`user.hash`** and a **config kill-switch** for PII enrichment — both considered and rejected
  above.
- **Metrics dimensions.** User id is deliberately NOT added as a metric tag (unbounded
  cardinality risk); attributes go on traces only.

## Divergence from plan (execution record — 2026-07-11)

Implemented on `feature/otel-user-attributes`. The plan was followed as written; deviations:

- **Primary enrichment approach used; fallback not needed.** `dotnet build` confirmed the
  `AspNetCoreTraceInstrumentationOptions.EnrichWithHttpRequest` signature (`Action<Activity,
  HttpRequest>`) is reachable, so the `Program.cs` `Configure<…>` path shipped and the documented
  `app.Use(...)` middleware fallback was **not** used.
- **ADR number is 0009** ("Enrich traces with the full OpenTelemetry `user.*` identity attribute set").
- **Extra robustness test added.** Beyond the plan's AC5 malformed/non-base64 case, added
  `GetUserTelemetryTags_PrincipalWithoutClaimsArray_DoesNotThrow` (valid base64+JSON blob with no
  `claims` array) to cover the defensive early-return branch — pushed new-code coverage to 98.8%.
- **Catch clause narrowed post-critic.** The `ParsePrincipalClaims` catch was trimmed from
  `FormatException or JsonException or DecoderFallbackException` to `FormatException or JsonException`:
  `Encoding.UTF8.GetString` uses replacement fallback and never throws `DecoderFallbackException`
  (dead-code cleanup flagged by the critic pass — non-blocking).
- **Email claim candidate list widened.** Added the `.../claims/upn` and `.../claims/emailaddress`
  schema-URI spellings alongside the short forms, since Easy Auth may emit either — still a prod-smoke
  fact per the new gotcha.
- **CLAUDE.md left unchanged.** The telemetry-enrichment knowledge is captured in ADR 0009 and the new
  `.agentic/gotchas.md` entry (prod-only claim-`typ` mapping); CLAUDE.md does not enumerate per-feature
  telemetry, so no new bullet was warranted.
- **Quality gate:** coverage 98.8% (coverlet, ≥80 ✅), vulnerable packages 0 ✅, `dotnet format` clean,
  107/107 tests pass. `duplication_pct_max` and `cognitive_complexity_max` reported **SKIPPED** — no
  measuring tool exists in this repo (self-reported per `quality-gates.yaml`); M2's shared
  `ResolveUserId` helper mitigates duplication by design.

## Review

Critic pass (fresh-context adversarial review). The extractor half, package/CPM handling, ADR
plan, composition claim, `EnrichWithHttpRequest` signature, `Activity.Current`-is-server-span
claim, and the `X-MS-CLIENT-PRINCIPAL` blob shape all **verified sound** — no ADR contradicted,
no gotcha tripped, `planned-against` matches HEAD.

- **B1 (blocker) — accepted & fixed.** The original AC6 wiring test was unsatisfiable: the repo
  has no `WebApplicationFactory`/`Mvc.Testing`, the `AgentApiTests` harness never runs `Program.cs`
  or OTel, and a bare `ActivityListener` doesn't invoke `EnrichWithHttpRequest`. Rewrote Step 3 and
  AC6 to prove wiring at the helper level (`ApplyUserTelemetryTags` on a directly-constructed
  `Activity`, asserted via `GetTagItem`) and moved "OTel invokes the callback on a real server
  span" to AC9 as an explicit prod-smoke check.
- **M1 (major) — accepted & fixed.** Same root cause as B1; the plan no longer claims an offline
  green ActivityListener test. The prod-only nature is now consistent across Context, Step 3 and AC9.
- **M2 (minor) — accepted & fixed.** Step 1 now factors the principal-id read + `local-dev`
  fallback into one private helper shared by `GetUserId` and `GetUserTelemetryTags` (no drift, no
  duplication against `duplication_pct_max: 3`).
- **M3 (minor) — accepted & fixed.** Step 1 / Context now call for a one-line rationale comment on
  the `IHeaderDictionary` receiver so the divergence from the file's `HttpContext`-receiver shape
  isn't "corrected" away.
