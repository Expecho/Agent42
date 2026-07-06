# Conversation history not rendered when selecting a conversation — COMPLETED

## Outcome

Selecting a conversation in the left pane showed no history. Fixed by two small changes;
verified end-to-end with a headless-browser repro and a new API integration test.

## Root cause (proven, not assumed)

The initial hypothesis — that the grain lost history across deactivation — was **wrong**.
An integration test that forces `DeactivateOnIdle()` and re-reads proved history survives
the session serialize/deserialize round-trip (`activations=2, historyCount=2`). A live
DevHost + curl confirmed every backend endpoint returns correct data.

The real cause is a frontend/API contract mismatch, caught by driving the actual UI
(Playwright) with in-page logging:

```
selectConversation threw SyntaxError: Unexpected token 'g', "gpt-4o" is not valid JSON
```

- `GET /api/conversations/{id}/model` returned the deployment as a **bare `string`**.
  ASP.NET minimal APIs serialize a bare string as `text/plain` (`gpt-4o`), not JSON.
- `selectConversation` (app.js) called `setModel(await modelRes.json())`, which **threw**
  parsing `gpt-4o`. That call sat in the same `try` and *before* the history-render loop,
  so the throw aborted rendering and the empty `catch` swallowed it — history silently
  never appeared. (The model picker also never restored, but the visible symptom was the
  missing history.)

## Changes

| File | Change |
|------|--------|
| `src/Agent42.Backend/AgentApi.cs` | `/model` wrapped in `Results.Json(...)` so it returns `application/json` like every sibling endpoint. |
| `src/Agent42.Backend/wwwroot/app.js` | `selectConversation` renders history *before* restoring the model picker, so a model-restore failure can never again hide the conversation. |
| `tests/Agent42.Tests/AgentApiTests.cs` | New: hosts the API on TestServer against the Orleans test cluster; asserts `/model` responds `application/json` (fails on the old code, passes on the fix) and `/messages` returns the history JSON. |
| `tests/Agent42.Tests/AgentGrainTests.cs` | Added `History_SurvivesGrainReactivation` (bonus regression: history survives deactivation via the persisted session). |
| `tests/Agent42.Tests/TestSiloConfigurator.cs` | Extracted the shared silo configurator so the API test can reuse it. |
| `tests/Agent42.Tests/Agent42.Tests.csproj` + `Directory.Packages.props` | Added `Microsoft.AspNetCore.TestHost` (pinned 10.0.9 via CPM) and a project reference to `Agent42.Backend` to enable in-memory HTTP-contract tests. |

## Verification

- `dotnet test Agent42.slnx` — all tests green; the new `/model` test fails on the pre-fix
  code and passes after (confirmed by temporary revert).
- Playwright drive of the real UI: send message → new conversation → reselect first →
  `history rendered: 2 messages shown`.
- `dotnet format --verify-no-changes` and `dotnet build` clean.

## Notes / follow-up

- Discarded ADR-0003 (it recorded an `InMemoryChatHistoryProvider` decision premised on the
  wrong root cause). No provider change was needed or made.
- Gotcha added: a minimal-API handler returning a bare `string` serves `text/plain`, not
  JSON — clients doing `.json()` will throw.
- The repo still has no JS/browser test harness in CI; the Playwright repro lives in the
  session scratchpad, not committed. Standing up a UI test harness is a possible follow-up.
