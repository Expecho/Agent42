---
planned-against: main @ cd8a4bd
---

# A shared read-only link must not require a login

## Overview

ADR 0035 shipped read-only share links whose whole point is "anyone with the link can open it,
no sign-in". In production **they still hit the Entra login page.** The Easy Auth ingress carve-out
in `infra/modules/container-app.bicep` lists `/api/share` and `/shared`, and ADR 0035 describes those
as *"two **prefixes** added to `globalValidation.excludedPaths`"* — but `excludedPaths` matches a path
**exactly** unless the entry ends in `/*`. Every URL a recipient actually loads is a sub-path, so every
one of them is gated.

A second, independent defect in the same feature: nothing calls `UseForwardedHeaders` anywhere in the tree
(`grep -rn "ForwardedHeaders" src` → no hits), so `HttpContext.Request.Scheme` is `http` inside the
container and `ShareUrl` (`src/Agent42.Backend/Sharing/AgentApi.Sharing.cs:79`) mints
`http://chat.agent42.nl/shared/#<token>`. Observed: `GET https://chat.agent42.nl/shared` returned
`301 → http://chat.agent42.nl/shared/`, i.e. the app's own `UseDefaultFiles` redirect built an `http` URL
while the request arrived over TLS.

Precision on impact, so the executor sizes the two defects correctly: the Easy Auth defect makes the
feature **non-functional** — every recipient hits the login page. The scheme defect is **cosmetic plus a
latent MCP bug** (see below), *not* an availability problem for sharing: `container-app.bicep` sets no
`allowInsecure`, so ACA 301s http→https and the fragment survives, and the token — being in the fragment —
never travelled over the plaintext hop.

Why now: the headline feature of ADR 0035 does not work at all in production.

## Affected areas

| Path | Change |
|---|---|
| `infra/modules/container-app.bicep` | `globalValidation.excludedPaths` entries + the comment above them |
| `src/Agent42.Backend/Program.cs` | add `UseForwardedHeaders` as the first middleware |
| `tests/Agent42.Tests/EasyAuthExclusionTests.cs` | **new** — the regression guard |
| `src/Agent42.Backend/wwwroot/shared/index.html` | comment only (lines 49–51 restate the wrong rule) |
| `.agentic/decisions/0035-read-only-share-links.md` | correct the "prefixes" claim |
| `.agentic/decisions/0002-entra-easy-auth-gate.md` | amendment note lists the holes |
| `.agentic/decisions/0042-*.md` | **new** ADR (see step 5) |
| `.agentic/gotchas.md` | correct the `excludedPaths` entry; add a forwarded-proto entry |
| `CLAUDE.md` | the ADR 0035 bullet's `excludedPaths` sentence |

Not touched: `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`, any grain, any Cosmos state, any
`wwwroot/js/` module (so no `sw.js` `CACHE` bump is owed — ADR 0041 gotcha). The handlers, the token, the
viewer's behaviour and the fail-closed 404s are all correct as shipped — only the ingress config, the
request scheme, and several comments/docs that assert the wrong matching rule are wrong.

## Context & constraints

### The matching semantics were measured, not inferred

Verified against the live app (`ca-agent42-backend`, `rg-agent42`, sub `De Heer MSDN`) on 2026-08-20 by
temporarily PUTting a widened `authConfigs/current` and curling. **Do not re-derive this; it is settled:**

| Path | with `['/api/share','/shared']` | with `['/api/share/*','/shared','/shared/*']` |
|---|---|---|
| `/shared` | 301 (reaches app) | 301 |
| `/shared/` | **401** | 200 |
| `/shared/viewer.js`, `/shared/viewer.css`, `/shared/lib/marked.min.js`, `/shared/lib/purify.min.js` | **401** | 200 |
| `/api/share/{token}` | **401** | 404 (the app's fail-closed) |
| `/api/share/{token}/attachments/{id}` | **401** | 404 |
| `/api/models`, `/api/conversations`, `/api/conversations/x/share` | 401 | 401 |
| `/api/sharezzz`, `/sharedzzz` (boundary probes) | 401 | **401** |
| `/api/share/../models`, `/shared/../api/models` (`curl --path-as-is`) | — | **401** |
| `/api/share/..%2f..%2fmodels`, `/shared/..%2fapi%2fmodels` | — | **401** |

Five facts this establishes, each load-bearing for the design:

1. A bare entry is an **exact** match — a prefix does *not* cover its sub-paths.
2. A `/*` entry **does** span multiple segments (`/api/share/*` covered `.../attachments/{id}`).
3. A `/*` entry matches an **empty** remainder (`/shared/*` covered `/shared/`).
4. The wildcard prefix **includes the trailing slash**: `/api/share/*` behaves as
   `StartsWith("/api/share/")`, not `StartsWith("/api/share")` — `/api/sharezzz` and `/sharedzzz` both
   stayed 401. So the entries cannot bleed onto a sibling route with a shared name stem.
5. **A wildcard entry is not a traversal bypass.** Dot-segments and encoded dot-segments under an
   excluded prefix do not smuggle an authenticated path through the exclusion — every probe stayed 401,
   both raw (`--path-as-is`) and `%2f`-encoded. This is the one new risk the wildcard introduces over
   exact matching, and it is measured, not assumed.

The wildcard form is documented in the App Service auth file-based configuration reference
(`"/path3/subpath/*"`); ACA runs the same Easy Auth middleware. The measurement above is what makes it
safe to rely on for Container Apps specifically.

**Production is currently running that hand-applied patch**, so share links work in prod *right now*.
That is drift: it exists only in ARM, not in Bicep, and the next `infra/main.bicep` deploy from
unmodified `main` would silently revert it. Step 1 is what makes it durable.

### Rejected alternative (do not re-litigate)

Enumerating every public URL as its own exact entry (`/shared/`, `/shared/viewer.js`, …) plus reshaping
the two public API routes so the token stops being a path segment. Rejected: it only used the
already-proven exact-match mechanism, but it pushes the token into a query string or a custom header
(reversing ADR 0035's deliberate fragment choice, or forcing the viewer to fetch images as blobs), and it
makes adding a file to `wwwroot/shared/` an infra change. The measurement above removed the only reason
to prefer it.

### The forwarded-proto fix has a second beneficiary — check it at deploy

`src/Agent42.Backend/Mcp/AgentApi.Mcp.cs:88` builds the MCP interactive-OAuth `redirect_uri` from the
same `Request.Scheme`, so it is currently sending `http://chat.agent42.nl/api/mcp/oauth/callback` while
CLAUDE.md records the registered URI as the `https://` one. This change flips it to `https://` — a
**repair**: ADR 0025's flow is very likely broken today (Entra refuses non-localhost `http://` web
redirect URIs outright). So step 7 asserts interactive OAuth *now* completes, not that it *still* does;
a failure there is a diagnosis, never a rollback signal.

**The one thing that does not self-heal** (`AgentApi.Mcp.cs:104-107`): when a server supports dynamic
client registration, the minted `ClientId` is **persisted on `StoredMcpServer` and reused forever** —
the code only registers when `ClientId` is blank. A client registered while `redirectUri` was `http://`
will reject the new `https://` authorization request with `invalid_redirect_uri`, permanently. The
remediation is to **clear the stored client id** for that server in Preferences → MCP servers, which
forces a re-registration on the next connect. This is a manual, user-facing step — put it in step 7 and
in the ADR's consequences, and do not attempt to auto-clear stored client ids as part of this change
(it would silently orphan registrations on servers that are working fine). Do not go editing provider
app registrations either (hard rule 5).

`KnownNetworks`/`KnownProxies` must be **cleared** — the ACA ingress IP is not knowable — which means a
client could spoof `X-Forwarded-Proto`. Accepted: the only consequences are the scheme of a minted share
URL and of a static-file redirect, and every external request arrives through the ingress that sets the
header. Record this in the ADR, not just in code.

### Governing docs to read first

- `.agentic/gotchas.md` — *"An Easy-Auth `excludedPaths` endpoint is publicly reachable and must
  self-enforce auth + fail closed"* (~line 1301). Its rule still holds; its *"verify at deploy"* line is
  exactly the discipline that was skipped, and its wording implies prefix matching.
- `.agentic/decisions/0035-read-only-share-links.md` (the "prefixes" claim, ~line 46), `0002`, `0022`.
- Exemplar for the new test: `tests/Agent42.Tests/ProjectLayoutTests.cs` — `RepoRoot()` (line 398) walks
  up to `Agent42.slnx` and is the way to reach `infra/` from a test.

### What is not testable

No test can exercise Easy Auth itself (TestServer has no ingress), and nothing in the suite runs
`Program.cs`. So the guard in step 2 asserts the **Bicep content** against the measured semantics, the
guard in step 3 is a source-text assertion in the style of the existing `*AssetsTests`, and the real
proof is the step 7 curl matrix against prod.

## Steps

Steps 1 and 3 touch disjoint files and are parallelizable; the change is small enough that sequential is
fine.

### Step 1 — Fix the ingress carve-out

`infra/modules/container-app.bicep`. Set:

```bicep
excludedPaths: [
  '/api/agent-run'
  '/api/share/*'
  '/shared'
  '/shared/*'
]
```

Drop the bare `/api/share`: it matches no route (it returned 404 when excluded), so it is a hole with
nothing behind it. Keep the bare `/shared` — it is what lets a hand-typed URL without the trailing slash
get the courtesy 301.

Rewrite the comment block above the list so it states the measured rule — a bare entry is an exact match,
`/*` spans sub-paths including a trailing-slash-only remainder — and keeps the existing per-path
self-enforcement notes. **Verify:** step 2's test, plus `az bicep build -f infra/main.bicep` if the
Bicep CLI is available.

### Step 2 — Guard it with a test

New `tests/Agent42.Tests/EasyAuthExclusionTests.cs`. Parse the `excludedPaths` array out of
`infra/modules/container-app.bicep` (locate `excludedPaths: [` and read the quoted entries until `]`),
and implement the measured matcher:

```
entry ends in "/*"  →  path.StartsWith(entry[..^1], OrdinalIgnoreCase)   // "/shared/*" → prefix "/shared/"
otherwise           →  path.Equals(entry, OrdinalIgnoreCase)
```

Note `entry[..^1]` keeps the slash — that is the measured behaviour (fact 4), and it is what makes
`/api/sharezzz` un-matched. A matcher that stripped `/*` would be wrong *and* would silently pass.

Assert:

- **Every public share URL is covered** — `/shared/`, `/api/share/{token}`,
  `/api/share/{token}/attachments/{id}` for a representative token.
- **Every same-origin URL the viewer actually requests is covered** — parsed out of
  `wwwroot/shared/index.html` (`src`/`href` attributes, skipping `data:` and absolute `https://` ones)
  and out of `viewer.js`'s two URL templates, resolved against `/shared/`, then run through the matcher.
  Do **not** write this as "every file under `wwwroot/shared/` is covered": `/shared/*` covers that
  tautologically, so such an assertion can never fail and merely restates the criterion above. The
  failure this must catch is the one `index.html:49-51` warns about — someone points the viewer at
  `/lib/marked.min.js` or `/styles.css` on the authenticated surface and a recipient gets a 401 again.
  Reuse `ProjectLayoutTests.RepoRoot()`'s approach to reach both trees.
- **No authenticated surface is covered** — `/`, `/index.html`, `/api/models`, `/api/conversations`,
  `/api/conversations/x/share`, `/api/providers`, `/api/usage` must all be *un*matched. This is the test
  that stops a future widening (e.g. `/api/*`) from silently opening the app.
- **The wildcard does not bleed onto a name stem** — `/api/sharezzz` and `/sharedzzz` must be unmatched
  (fact 4 above).
- `/api/agent-run` stays covered (ADR 0022 must not regress).

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~EasyAuthExclusion` — and sanity-check
the matcher by asserting it *fails* for the old list (a test case that feeds the matcher the literal old
`['/api/agent-run','/api/share','/shared']` and asserts `/shared/` is unmatched — this pins the bug
itself, not just the fix).

### Step 3 — Trust the ingress's forwarded proto

`src/Agent42.Backend/Program.cs`, as the **first** middleware — before
`app.Use(UserTelemetryMiddleware.InvokeAsync)` and before `UseDefaultFiles`/`UseStaticFiles`, so both the
static-file redirect and `ShareUrl` see the corrected scheme:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto,
    KnownNetworks = { },
    KnownProxies = { },
});
```

Needs `using Microsoft.AspNetCore.HttpOverrides;` at the top of `Program.cs`.

`XForwardedProto` **only** — do not add `XForwardedFor`/`XForwardedHost`; nothing needs the client IP and
trusting those from an unknown proxy is a strictly larger surface. The default `ForwardLimit = 1` is
correct (a single ACA ingress hop) — leave it and say so. Carry a comment explaining why the known-proxy
lists are cleared and what the spoofing consequence is bounded to.

**Also fix the comment directly below it.** `Program.cs:419` opens *"First in the pipeline: extract the
request's identity once…"* — that sentence becomes false the moment this middleware goes in front of it.
Reword it (the telemetry middleware is still first *after* the proxy fix-up, and its own ordering
rationale is unchanged).

Add a source-text assertion (extend `tests/Agent42.Tests/ProjectLayoutTests.cs` or a small new test) that
`Program.cs` calls `UseForwardedHeaders` with `XForwardedProto`, and that it appears **before**
`UserTelemetryMiddleware` in the file. **Verify:** `dotnet build Agent42.slnx` and the new test.

### Step 4 — Pay the doc debt (same MR, hard rule 2)

- `.agentic/decisions/0035-read-only-share-links.md`: the "two **prefixes** added" sentence (~:46) is
  factually wrong. Line ~:53 restates the same wrong rule in different words — *"the Easy Auth hole
  stays exactly `/shared` + `/api/share`"* — so a `prefix` grep will not find it. Fix both and add a
  short amendment note pointing at the new ADR.
- `.agentic/decisions/0002-entra-easy-auth-gate.md:10-14`: this is **not** an enumeration to increment —
  it still says `/api/agent-run` is *"the single hole"* and was never updated for ADR 0035. Rewrite it
  for the current four entries.
- `.agentic/gotchas.md`: correct the `excludedPaths` entry (~:1301) — it implies prefix matching and says
  there are three holes. Add the measured rule with the 2026-08-20 evidence. While in there, fix its
  evidence line (~:1318): it cites `StoredAgents/AgentApi.StoredAgents.cs` for `/api/share/{token}`, but
  ADR 0040 moved that route to `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs:42`. The CI doc-path job
  cannot catch this — `check-doc-paths.mjs` only asserts the file exists, not that the claim is true.
  Then add a second entry for the forwarded-proto trap (`Request.Scheme` is `http` behind ACA ingress;
  two call sites depend on it, one of which persists a client id derived from it).
- `src/Agent42.Backend/wwwroot/shared/index.html:49-51`: the comment asserts *"the Easy Auth hole stays
  exactly /shared + /api/share"*. Correct it in place — the self-containment rule it is really defending
  is still right and is now machine-checked by step 2's test, so point it there.
- `CLAUDE.md`: the ADR 0035 bullet's `excludedPaths` sentence.

**Verify:** the CI doc-path-integrity job, plus
`grep -rn "prefix\|/api/share.*+.*shared\|single hole" .agentic/decisions/0002-*.md
.agentic/decisions/0035-*.md .agentic/gotchas.md CLAUDE.md src/Agent42.Backend/wwwroot/shared/index.html`
— read every hit, don't just count them.

### Step 5 — Record the decision (`/decide`)

One ADR, `0042`, amending 0035 and 0002, covering both halves. Status `accepted`, tags reusing 0035's
vocabulary (`easy-auth`, `excluded-paths`, `sharing`, `infra`).

**Half 1 — wildcard `excludedPaths`.** Carry the measurement table verbatim; it is the evidence and this
repo has no other way to test ingress behaviour. Rejected alternative: exact-path enumeration (see
above).

**Half 2 — trusting `X-Forwarded-Proto`.** Name and refute the two real alternatives, not just the
chosen one:
- *A configured public base URL* (e.g. `App:PublicBaseUrl`) — pins scheme **and** host and trusts no
  spoofable header at all, which would make the spoofing acceptance unnecessary. Refuted on: a new
  mandatory config input on both hosts (the trap ADR 0029 documents), a value that must be kept in sync
  with the ingress FQDN by hand, and it does not fix the framework's own static-file redirect.
- *`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in Bicep* — zero code. Refuted on: it enables
  `XForwardedFor` and `XForwardedHost` too, a strictly larger trust surface than the two consumers need,
  and it puts a load-bearing behaviour in an env var rather than in reviewable code.

**Consequences to record explicitly:**
- The MCP dynamic-registration `ClientId` staleness above — the one non-self-healing effect.
- `/api/share/*` lets an unauthenticated caller force a `ShareLinkDirectoryGrain` activation plus a
  Cosmos read per distinct token guess, and there is no rate limiter anywhere in the app (`grep -rn
  "RateLimit" src` → no hits). Same accepted property as ADR 0022's `/api/agent-run`, and 256-bit tokens
  make guessing pointless — but this is the first time it is reachable by design from shipped Bicep, so
  state it rather than inherit it silently.

### Step 6 — Gates and MR

Branch `fix/share-link-requires-login`. Run `dotnet test Agent42.slnx`,
`dotnet format Agent42.slnx --verify-no-changes`, `dotnet build Agent42.slnx`. Report the actual output.
Open the MR.

### Step 7 — Post-deploy verification (needs the user; cannot run in CI)

After the deploy workflow completes, run and paste the output:

```bash
for u in /shared/ /shared/viewer.js /shared/lib/marked.min.js \
         /api/share/bogus /api/share/bogus/attachments/x \
         /api/models /api/conversations / \
         /api/sharezzz /sharedzzz; do
  printf "%-42s " "$u"; curl -s -o /dev/null -w "%{http_code}\n" "https://chat.agent42.nl$u"
done
curl --path-as-is -s -o /dev/null -w "traversal   -> %{http_code}\n" "https://chat.agent42.nl/api/share/../models"
curl -s -o /dev/null -w "encoded     -> %{http_code}\n" "https://chat.agent42.nl/api/share/..%2f..%2fmodels"
curl -s -o /dev/null -w "redirect    -> %{redirect_url}\n" "https://chat.agent42.nl/shared"
```

Expected: `200 200 200 404 404 401 401 401 401 401`, both bypass probes `401`, and the redirect target now
`https://…/shared/` (that last one is the proof step 3 landed — nothing else observes it). Then mint a
real link from the UI, confirm it starts with `https://`, and open it in a private window with no Entra
session.

Finally, MCP interactive OAuth. Assert it **now** completes — it is probably broken today, so a failure
here is not a rollback signal. If a server that used dynamic client registration fails with
`invalid_redirect_uri`, that is the persisted-`ClientId` staleness described above: clear that server's
client id in Preferences → MCP servers and reconnect. Report which servers needed it.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | The viewer page URL `/shared/` is carved out of Easy Auth | `Exclusions_CoverTheViewerPage` | `tests/Agent42.Tests/EasyAuthExclusionTests.cs` |
| 2 | Every same-origin URL the viewer requests is carved out — parsed from `index.html` + `viewer.js`, so pointing the viewer at the authenticated surface fails the build | `Exclusions_CoverEverySameOriginUrlTheViewerRequests` | same |
| 3 | Both public share API routes, incl. the multi-segment attachment route, are carved out | `Exclusions_CoverBothPublicShareRoutes` | same |
| 4 | No authenticated surface is carved out | `Exclusions_DoNotCoverAnyAuthenticatedPath` | same |
| 5 | `/api/agent-run` (ADR 0022) still carved out | `Exclusions_StillCoverAgentRun` | same |
| 6 | The exact-match semantics are pinned, so the old bare-prefix list is provably broken | `BarePrefixEntry_DoesNotCoverSubPaths` | same |
| 7 | The wildcard does not bleed onto a name stem (`/api/sharezzz`) | `WildcardEntry_DoesNotMatchANameStem` | same |
| 8 | A minted share URL is `https://` behind the ingress | `Program_TrustsForwardedProtoBeforeAnyOtherMiddleware` (source-text) + step 7 redirect probe | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| 9 | An unauthenticated recipient can actually open a real link | step 7 manual (not automatable — no Easy Auth in TestServer) | — |
| 10 | A wildcard exclusion is not a traversal bypass | step 7 probes (ingress behaviour — not automatable) | — |
| 11 | MCP interactive OAuth completes on the corrected `https` redirect URI | step 7 manual, with the `ClientId`-clearing remediation | — |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold |
|---|---|
| `new_issues` | 0 |
| `new_security_issues` | 0 |
| `duplication_pct_max` | 3 |
| `coverage_new_code_pct_min` | 80 |
| `cognitive_complexity_max` | 15 |
| `vulnerable_packages` | 0 (hard block) |
| Protected branches | `[main]` — always a `fix/*` branch + MR |
| Local enforcement | `soft_warning` (render the table, ask proceed/abort on FAIL) |

Coverage note: the new test file is the only new *executable* code and is fully exercised by its own
cases. The `Program.cs` middleware line is not reachable by any test (nothing runs `Program.cs`) — report
that as evidence-based justification rather than a coverage miss.

## Out of scope

- Redesigning the public routes so the token leaves the URL path (query string / `X-Share-Token` header
  and blob-fetched images). It would keep the token out of ingress access logs, but that is an ADR 0035
  property, not this bug.
- Anything about ADR 0035's egress consequence (a token exposes the full live history to whoever holds
  the link). Unchanged here.
- Service-worker handling of `/shared/` — the SW deliberately never touches `/api` (ADR 0032) and the
  viewer is outside the shell; leave it alone.
- Link expiry, per-link passwords, or view counters.
- Auto-clearing or migrating stored MCP `ClientId`s, and any change to MCP OAuth itself. The scheme flip
  repairs the redirect URI; the stale-client-id case is handled by the documented manual remediation.
- Rate limiting the public share routes.
- Restoring the hand-patched prod `authConfig` to the old broken list. It is drift, and step 1 + the
  deploy resolve it; reverting first would only re-break sharing for no benefit.

## Review

A `critic` pass ran against the draft. It could not break the core: it independently confirmed the four
entries cover every same-origin URL the viewer requests, that nothing authenticated is widened (it dumped
the full route list — `/api/conversations/{id}/share` does not start with `/api/share`, and nothing lives
at `/shared*` outside `wwwroot/shared/`), that dropping bare `/api/share` is safe, and that the
`UseForwardedHeaders` placement is right (the 301 comes from `UseDefaultFiles` via
`UriHelper.BuildAbsolute(request.Scheme, …)`, nothing branches on `IsHttps`, `MapDefaultEndpoints` maps
health endpoints only under `IsDevelopment()`, and the OTel server span captures `url.scheme` before any
middleware).

**Findings adopted** — the plan above already reflects all of them:

| # | Finding | How it was handled |
|---|---|---|
| 1 | MCP dynamic-registration `ClientId` is persisted and reused forever, so the scheme flip breaks it permanently with no self-healing | New paragraph in Context; step 7 carries the clear-the-client-id remediation; ADR consequence. Also corrected step 7's assertion from "still completes" to "now completes" |
| 4 | "every file under `wwwroot/shared/` is covered" is tautological under `/shared/*` and cannot fail | Step 2's assertion rewritten to parse `index.html` + `viewer.js` for same-origin URLs; AC#2 reworded |
| 5 | Three claims the change invalidates were unlisted: `Program.cs:419` "first in the pipeline", `index.html:49-51` "the hole stays exactly /shared + /api/share", and a gotcha evidence path stale since ADR 0040 | All three added — `wwwroot/shared/index.html` promoted into Affected areas; step 3 and step 4 name each fix |
| 6 | ADR 0042's forwarded-proto half had no rejected alternatives | Step 5 now names and refutes a configured public base URL and `ASPNETCORE_FORWARDEDHEADERS_ENABLED` |
| 7 | The scheme defect had no evidence path, and "100% non-functional" overstated it | Overview now carries the evidence and separates the two defects' impact |
| 8 | The step 4 `prefix` grep misses `0035:53`, and ADR 0002 says "the single hole" — a rewrite, not an increment | Step 4 rewritten with the wider grep and the correct characterisation |
| 9 | Unauthenticated grain activation + Cosmos read per token guess, with no rate limiter anywhere | Added as an explicit ADR consequence |
| 10 | Missing `using`; `ForwardLimit` worth a word | Both in step 3 |

**Findings resolved by measurement rather than by editing prose** (both were raised against a draft that
predated the probes; the plan now carries the evidence):

- **#2 — the wildcard's prefix boundary was assumed, not measured.** Fair against the draft. Probed:
  `/api/sharezzz` and `/sharedzzz` both stay 401, so the prefix demonstrably includes the trailing slash.
  Now fact 4, an AC, and a step 7 probe.
- **#3 — a wildcard makes `/api/share/../models` conceivable in a way exact matching did not.** Correct,
  and worth taking seriously. Probed raw (`--path-as-is`) and `%2f`-encoded, against both prefixes: all
  401. Now fact 5, AC#10, and a step 7 probe.

**Nothing was rejected.**

## Divergence from the plan (recorded at execution, 2026-08-20)

The plan was executed as written — all seven steps, no step dropped, no rejected alternative revisited.
Steps 1–6 are complete; **step 7 is post-deploy and still owed** (it needs the user and a deployed
revision). What differed:

**1. The viewer comment fix was constrained by an existing test the plan did not name.**
`ShareLinkAssetsTests.Viewer_IsSelfContained_NoAuthenticatedAssets` asserts
`DoesNotContain("styles.css")` and `DoesNotContain("/lib/")` against the **whole served page, comments
included**. The step-4 rewrite of `wwwroot/shared/index.html:49-51` named those very strings to explain
what must not be linked, and failed that test. Reworded to say "the authenticated app's own lib folder or
stylesheet" instead. Caught by the full suite, not by the narrow filtered run — a small argument for the
plan's own step-6 discipline.

**2. One extra doc site, beyond the five the plan listed.**
`.agentic/decisions/0035-read-only-share-links.md:85` (in *Alternatives considered*) also asserted the
wrong rule — *"keeps the auth hole to two tight prefixes"*. It survived the plan's step-4 grep because
that grep only covered `0002`, `0035`, `gotchas.md`, `CLAUDE.md` and `index.html` for the phrasings it
predicted; this one is a third wording in a section nobody expected to be load-bearing. Fixed, and pointed
at the new machine check.

**3. ADR 0042's measurement table had to un-backtick its URL paths.**
`.agentic/scripts/check-doc-paths.mjs` treats any backticked token containing a slash and a lowercase
extension as a **repo path** and fails CI when it does not exist. Carrying the measurement table verbatim
(step 5's instruction) therefore broke the doc-path job on `/shared/viewer.js`, `/shared/viewer.css`, the
two vendored libs and `/lib/marked.min.js` — which are URLs, not files. They are now plain text in the
table. The table's content is unchanged; only its markup is.

**4. `dotnet build /t:Metrics` is not available in this repo** (the `Metrics` target does not exist in the
projects), so the complexity number in the gate table comes from `lizard`, the documented fallback in
`.claude/skills/dotnet-complexity`. Note also that the configured gate is `cognitive_complexity_max`,
while both available tools measure **cyclomatic** complexity — see the gate table's note.

**5. A line-ending trap worth knowing before the next scripted doc edit.** The repo is stored LF in git
and checked out **CRLF**; `dotnet format` enforces CRLF and fails on every line of a converted file. A
scripted rewrite that reads a file in text mode and writes it back silently converts the whole file to LF
— which turns a 20-line edit into a whole-file diff and a wall of `error WHITESPACE`. Restored before the
gates were run, so the final diff is minimal; captured as a gotcha.

**7. The plan's own `Program.cs` snippet was wrong, and the critic caught it.** Step 3 prescribed
`KnownNetworks = { }` / `KnownProxies = { }` inside the object initializer. Those properties are
**get-only**, so that syntax parses as a nested *collection initializer* that adds nothing — it compiles,
reads as "cleared", and leaves the framework defaults (`127.0.0.0/8`, `::1`) in place. Behind a
non-loopback ingress the middleware would have **ignored `X-Forwarded-Proto` entirely**, leaving half 2
inert while the code, the ADR and the new gotcha all claimed the lists were cleared — and the source-text
guard as first written asserted only the flag and the ordering, so it passed either way. Measured with a
throwaway `dotnet run app.cs` spike: the initializer leaves `KnownNetworks.Count == 1` /
`KnownProxies.Count == 1`; `.Clear()` empties them; `KnownIPNetworks` and the legacy `KnownNetworks` share
one backing list; `ForwardLimit` defaults to 1 as the plan said. Now built as a local, cleared with
`KnownIPNetworks.Clear()` (the non-obsolete API — `KnownNetworks` raises `ASPDEPR005` on .NET 10), and the
guard asserts the `.Clear()` calls and rejects the `= {` form. **This is the one place the plan was
materially wrong rather than merely incomplete.**

**8. Five smaller critic findings, all adopted.** (a) A sixth invalidated doc claim the plan did not list —
`README.md`'s carve-out enumeration, still naming `/api/agent-run` as the only one. (b) A seventh site
repeating the refuted phrase, in `tests/Agent42.Tests/ShareLinkAssetsTests.cs`. (c) The new markup comment
overstated novelty — `ShareLinkAssetsTests` has machine-checked self-containment since ADR 0035; the two
tests are now described as the complementary halves they are. (d) `ResolveAgainstViewer` concatenated
rather than normalised, so a `../lib/…` subresource resolved to `/shared/../lib/…` and was reported
*covered* while the browser would get a 401 — now resolved through `Uri` so dot-segments collapse. (e) The
wildcard's standing hazard (anything added under `/api/share/` is public by construction) was unrecorded —
now an ADR consequence and a comment where a route author will read it. An eighth, pre-existing item was
fixed in passing: the orphan ADR 0035 comment block left behind in
`src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` by the ADR 0040 move.

**9. Not a divergence, but recorded because it shaped the run:** a `Agent42.DevHost` process was holding a
lock on `Agent42.Backend.dll`, which makes the solution-wide `dotnet build`/`dotnet test Agent42.slnx` fail
on the copy step while compilation itself succeeds. Stopped (with the user's agreement) before the gates
were run, so the gate evidence is from the verbatim CLAUDE.md commands.
