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
| _(none yet)_ | | |

The Google Calendar guide lived here until ADR 0063 moved those tools to a standalone MCP server
outside this repo. The calendar is now reached like any other remote MCP server (ADR 0024): add it
in Preferences → MCP servers and connect it there. Its own setup guide lives with that server.

**No page here contains a secret value.** Same discipline as `ConfigHealth`: name the setting, never
its value — and no screenshot showing one.
