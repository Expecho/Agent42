---
id: 0009
date: 2026-07-11
status: accepted
tags: [telemetry, observability, privacy, auth]
---

# Enrich traces with the full OpenTelemetry `user.*` identity attribute set

> **Amended by [ADR 0074](0074-identity-alias-for-a-second-entra-account.md) (2026-09-13).**
> `user.id` is no longer necessarily the requesting principal's Entra object id: an aliased account
> reports the object id it resolves to. The other five attributes are unchanged — `user.name` comes
> from the principal-name header and `user.email`/`user.full_name`/`user.roles` from the claims blob,
> none of which passes through the alias — so every span still names the account that signed in.

> **Extended by [ADR 0077](0077-app-insights-export-content-stripped.md) (2026-09-19).** The PII
> trade-off below names "both OTLP collectors". Application Insights is now a **third** sink for the
> `user.*` attributes: they are on its allowlist, while chat content is stripped.

## Context
Agent42 is a multi-user system (two Entra users today, behind Easy Auth — ADR 0002)
whose traces carried **no user attribution**: a span could not be tied to whose request
produced it. Telemetry is auto-instrumentation only; the sole enrichable span for an
incoming request is the ASP.NET Core **server span**. Both OTLP collectors the app
already exports to (the env-var endpoint and the config-driven second collector) would
receive whatever we stamp. The interview question was how much identity to attach.

## Decision
Stamp the full semantic-convention set on the server span of every authenticated request,
extracted from the Easy Auth headers in `CurrentUser.cs`: `user.id`, `user.name`,
`user.email`, `user.full_name`, `user.roles` (a multi-value array), plus a custom
`user.idp`. Applied via `AspNetCoreTraceInstrumentationOptions.EnrichWithHttpRequest`
configured from Backend `Program.cs`, so ServiceDefaults stays generic. Traces only.

## Alternatives considered
- **`user.id` only** (attribution by Entra oid, names resolved externally) — least PII, but
  every trace lookup then needs an out-of-band oid→identity join; the user chose "store as
  much as possible" for a two-user personal app.
- **`user.id` + name/email** — a middle ground; rejected for the same reason (partial data
  still needs the external join for roles/idp).
- **Add a stable non-PII `user.hash`** alongside — extra code for a correlation key that oid
  already provides here; YAGNI at two users.
- **A config kill-switch** to disable PII enrichment per environment — YAGNI for a personal
  app with no external consumers; can be added if a real multi-tenant need appears.

## Consequences
- Traces are now attributable per user without an external join.
- **PII (name, email, roles) leaves the app to both OTLP collectors** — an accepted trade-off
  for this personal, two-user, allowlisted app; revisit if consumers or users are ever added.
- `user.idp` is a deliberate **non-semconv** custom key (there is no semantic-convention key
  for the identity provider) — a documented deviation.
- The Easy Auth claim `typ` spellings are a **prod-only** fact (Easy Auth is absent locally),
  so the claims→attribute mapping is verified by prod smoke, not offline tests; a wrong `typ`
  match is a `/gotcha` to update, not a code bug.
- Out of scope (fast-follows if wanted): request-scoped log enrichment, outbound/grain-span
  enrichment (they run off the request execution context — **now delivered in ADR 0010**, which
  propagates the identity to downstream/in-grain spans via Orleans `RequestContext`), and metric
  dimensions (unbounded user cardinality is deliberately avoided).
