# Integration setup guides

Some features need something set up **outside this repository** before they work: an account
somewhere, an app registration, a redirect URI, a pair of GitHub Actions secrets. The code cannot do
any of that, and a plan spec that describes it moves to `.agentic/specs/completed/` and stops being
anybody's reference.

So each integration that needs manual setup gets a page here: what to create, where, in what order,
and what the symptom looks like when a step is skipped.

This is a sibling of [`docs/features/`](../features/README.md), and the split is deliberate:

- **`docs/features/`** answers *how does this work?* — the implementation, for someone changing it.
  Flat, one page per feature.
- **`docs/integrations/`** answers *what do I have to set up?* — the console clicks, for someone
  deploying it. One **folder** per integration, because a setup guide accumulates screenshots and
  console walkthroughs and they need somewhere to live.

| Integration | Guide | Enables |
|---|---|---|
| Google Calendar | [google-calendar/](google-calendar/README.md) | The six calendar tools (ADR 0053) — list, read, create, change and delete the user's events |

**No page here contains a secret value.** Same discipline as `ConfigHealth`: name the setting, never
its value — and no screenshot showing one.
