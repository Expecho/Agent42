# Plan cc-03: stop four silent exception swallows

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md` (read its "Shared constraints" first)

## Overview

146 `catch (Exception` sites; almost all log and carry a comment saying why. Four swallow with no trace. This is a small
`fix/*` MR. **No catch-and-log helper** is introduced (decision in the master plan): per-site policy is the point.

## Affected areas (scout-verified; re-read each before editing)

- `src/Agent42.Backend/Export/ConversationZipWriter.cs:68-71` — `when ex is not OperationCanceledException` then
  `stored = null;`. **Correction (critic):** the failure is already surfaced to the user: `TryCopyBlobAsync` adds the entry to
  `manifest.Missing` right after the catch (`:73-77`). The only gap is the **log line** (no MISSING.txt question exists).
  The class is `static` with no logger: callers are `AgentApi.Export.cs:66/67/140/143`: thread an `ILogger` (or a
  `Action<string, Exception>`) from those.
- `src/Agent42.Backend/Fleet/FleetDigest.cs:86-89` — returns `[]` with only a comment. Also a `static` class with no logger;
  its call site is `FleetLessonService.cs:~494` (find it): pass the logger from there.
- **Scope note:** the "146" scan counts `catch (Exception`; there are also ~18 bare `catch { }` sites under `src/`. Review those
  in the same pass and list any that swallow with no log and no justification; fix those you find, or record them as a follow-up.
- `src/Agent42.Agents/UserSkills/SkillPackage.cs:177-179` and `:221-223` — return `null`, no log.
- Re-run the scan to confirm there are exactly these four: grep `catch (Exception` with the following 3 lines containing
  no `_logger`/`logger.`/`Log`/`throw`/`return ex` (scout used a manual read, not a tool; a second pass may find a fifth).

## Context & constraints

- **Log, don't rethrow.** Each site is best-effort by design; keep the behaviour (returns null/[]) and add a
  `LogWarning`/`LogDebug` with the exception and *identifiers only*. **Never log content**: export carries the owner's
  conversation, skills carry user text, and the repo has an explicit rule for push text (`PushTool_DoesNotLogNotificationText`).
  Log file name/skill name/ids, never bodies.
- The export already reports the missing attachment in the manifest; no behaviour question remains. Log only.
- `SkillPackage` is a static/pure-ish parser: check whether it has a logger; if not, prefer returning a result carrying the
  reason (the callers already branch on null) over threading a logger into a static. If that widens the signature across
  more than 3 call sites, log at the caller instead and say so.
- ADR 0061 ("swallowed on purpose") is the prior art for best-effort + logged; grep `.agentic/decisions/` first.

## Steps

1. Branch `fix/cc-03-silent-catches`. Write a failing test per site that asserts a log entry is produced. **There is no shared
   log-capture helper**: six private copies exist (`ListLogger`, `CapturingLogger<T>`, `RecordingLogger`, `CollectingLogger`, …) and
   `PushToolLimitsTests.cs` says no seam exists. Do **not** add a seventh: create one shared `TestLogger` in `tests/Agent42.Tests/`,
   use it here, and (optional, same MR if small) point the six copies at it.
2. Add the log lines. Verify the tests pass and the behaviour assertions (existing tests) are unchanged.
3. Add a source-text guard `NoCatchException_SwallowsSilently` only if it can be made precise (a regex over `catch (Exception`
   followed by a block with none of: logger call, `throw`, `return` of the exception, a `// ` justification **and** a log).
   If it can't be made precise without false positives, skip the guard and say so — do not add a flaky test.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Unreadable attachment is logged, export still completes and lists it as missing | `Export_LogsAnAttachmentItCouldNotRead` | `ConversationExportApiTests.cs` or `ConversationExportAssetsTests.cs` (confirm which with `ls tests/Agent42.Tests \| grep -i export`) |
| Digest failure is logged, returns empty | `FleetDigest_LogsAndReturnsEmpty_OnFailure` | find the digest test file (`grep -rln FleetDigest tests/`); create `FleetDigestTests.cs` if none |
| Malformed skill package is logged without its content | `SkillPackage_LogsWithoutContent` | find (`grep -rln SkillPackage tests/`); likely `SkillPackageTests.cs` |

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean.

## Out of scope

A shared helper; changing swallow→rethrow; the export MISSING.txt behaviour (ask, don't do).

## Review
