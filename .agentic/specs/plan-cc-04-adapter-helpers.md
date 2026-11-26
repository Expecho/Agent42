# Plan cc-04: share the real duplication across chat platform adapters

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md` (read its "Shared constraints" first)

## Overview

Telegram, Slack, Discord and Signal adapters share a lot already (chunking, retry/pacing, dedupe, commands, the adapter
interfaces). Three genuine duplicates remain. This plan extracts exactly those and **nothing else**: markdown conversion
and mention parsing stay per-platform on purpose.

## Affected areas (scout-verified; all under `src/Agent42.Backend/ChatGateway/`)

- **Byte-capped download loop**, copy-pasted: `Telegram/TelegramBotApi.cs:100-123`, `Slack/SlackWebApi.cs:205-220`
  (`ReadCappedAsync`), `Discord/DiscordRestApi.cs` ~132-165 (81,920-byte chunks, `buffer.Length + read > maxBytes`,
  `ContentLength > maxBytes` pre-check). Signal does a base64-length check instead (`Signal/SignalRpcClient.cs:192-212`) — leave.
- **Status → `ChatSendErrorKind` mapping.** **Rescoped (critic, verified):** only **Telegram and Discord** map an HTTP status
  (`TelegramBotApi.cs:24-34`, `DiscordRestApi.cs:22-31`; common core 429→RateLimited, 403→Forbidden, 5xx/0→Transient, else Unknown;
  Telegram's 400-text cases come *before* 403 and Discord adds 404→NotFound, so the shared function must take ordering and
  extra cases from the caller). **Slack** maps string error codes from a 200 body (`SlackWebApi.cs:28-36`) and **Signal** maps
  JSON-RPC codes (`SignalRpcClient.cs:363-389`): leave both alone. If the shared piece ends up smaller than the code it
  replaces, **drop this step** and say so in the MR rather than force it.
- **Named-HttpClient registration with `RemoveAllResilienceHandlers` + `#pragma warning disable EXTEXP0001`**:
  `TelegramBotApi.cs:69-84`, `SlackWebApi.cs:61-69`, `DiscordRestApi.cs:62-70`, `SignalRpcClient.cs:60-71`,
  `SignalDaemon.cs:478-481`. Telegram span suppression (`:187-196`) and Signal `SuppressInstrumentationScope` are hooks.
- New shared files go in `ChatGateway/` (next to `ChatMessageChunker.cs`).
- Tests: `TelegramTests.cs`, `SlackTests.cs`, `DiscordTests.cs`, `SignalRpcTests.cs`, `SignalAdapterTests.cs` (behavioural).
  `ProjectLayoutTests.cs:~922` `EveryNamedHttpClient_RemovesTheInheritedRetryPipeline` scans **all** `src/**/*.cs` for
  `AddHttpClient` and requires `RemoveAllResilienceHandlers`: the helper must keep that literal visible to the scanner
  (either the helper itself contains the `AddHttpClient`+`RemoveAllResilienceHandlers` pair and the test's allowlist/regex is
  updated deliberately, or the call sites keep `AddHttpClient` and the helper takes the builder). Read the test first and
  choose the option that keeps the guard meaningful; CLAUDE.md cross-cutting rule 8 is why it exists.

## Context & constraints

- Signature sketches (decide in step 1, not binding): `BoundedResponseReader.ReadCappedAsync(Stream, long maxBytes, ct)`
  returning bytes or a typed "too large"; `ChatSendErrors.FromStatus(int status)` returning the common kind, which each
  `ToSendResult()` then refines; `IHttpClientBuilder.AddChatPlatformClient(...)` or similar.
- Parameter-heavy methods (`TelegramBotApi.UploadAsync` 8, `SignalRpcClient.SendAsync` 8,
  `DiscordRestApi.CreateMessageWithFileAsync` 7) have those params for platform reasons (token in URL, method name,
  form fields, file part; signal-cli JSON-RPC mirror). Fold the file-part values into **one small record per API**
  (`UploadFile(Bytes, FileName, MediaType)`) — that removes 2–3 params each without hiding platform detail. Do not invent a
  cross-platform "upload request" abstraction.
- The download cap is a security property (memory exhaustion from a hostile attachment): the shared reader must behave
  identically, including the pre-check and mid-stream abort. Port the existing tests to the shared type and keep each
  adapter's behavioural test.
- Egress rule (CLAUDE.md rule 13): this plan changes no egress.

## Steps

1. Read the three download loops side by side; write `BoundedResponseReader` with tests that cover: under cap, exactly at
   cap, one over, `Content-Length` over (no read), no `Content-Length` and stream over cap. Verify
   `dotnet test --filter BoundedResponseReader`.
2. Switch Telegram, Slack, Discord to it (one commit each). Verify each adapter's test class.
3. (Optional, see rescoping) extract the HTTP-status core for Telegram + Discord only. Verify `TelegramTests`, `DiscordTests`.
4. Extract the HttpClient registration helper per the `ProjectLayoutTests` decision above; update that test only if its
   *mechanism* must change, keeping the same guarantee. Verify `ProjectLayoutTests`.
5. Fold the file-part params into per-API records. Verify the three upload tests.
6. Re-run `code-shape` (plan 01) and record before/after for the touched files in the MR.

Steps 2/3/5 are parallelizable by adapter only in separate worktrees; default sequential.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Shared reader enforces the cap exactly as the three copies did | `BoundedResponseReader_*` (5 cases above) | `BoundedResponseReaderTests.cs` (new) |
| Telegram/Discord status mapping unchanged (if step 3 is done) | existing error-mapping cases pass unchanged; add a table test for the shared core | `ChatSendErrorsTests.cs` (new) |
| Every named client still drops the inherited retry pipeline | `EveryNamedHttpClient_RemovesTheInheritedRetryPipeline` | `ProjectLayoutTests.cs` |

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean; no new package.

## Out of scope

Shared markdown/mention code; a base adapter class; supervisor backoff unification (Slack/Signal/Discord classify
permanent vs transient differently — record as a possible follow-up only); ChatTurnPipeline (plan 05).

## Review
