# Spike — Telnyx AI Assistant with a custom LLM (phone option B)

Status: **not started** · written 2026-10-03 · feeds a rewrite of `plan-phone-call-your-agent.md`

## Why this spike exists

`plan-phone-call-your-agent.md` is blocked: ACS phone numbers are retired for new customers
(ACS retirement guide, announced September 2026; new ACS resources cannot acquire numbers, and
Call Automation is standalone-supported only until 2028-09-30). Of the alternatives researched,
**option B** is the cheapest one that keeps `AgentGrain` as the brain *and* delivers speech
barge-in and streamed replies:

```
caller ──PSTN──► Telnyx number ──► Telnyx AI Assistant (STT, TTS, turn-taking, interruption)
                                          │  POST {baseUrl}/chat/completions  (OpenAI-compatible)
                                          │  Authorization: Bearer <integration secret>
                                          │  extra_metadata: { telnyx_end_user_target, … }
                                          ▼
                              Agent42 endpoint ──► AgentGrain.ChatAsync ──► streamed text back
```

Telnyx documents the endpoint only as "any OpenAI-compatible chat completions endpoint". It
does **not** document message-history behavior, streaming requirements, timeouts, tool
handling or a per-call identifier. Those undocumented details decide whether this maps onto a
stateful, non-reentrant grain. The spike answers them with captured evidence, not docs.

**Budget:** about half a day, plus ~USD 15–20 of Telnyx credit.
**Fallback if B fails:** option A (Telnyx Call Control `transcription_start` + `speak`,
turn-based, keeps the current plan's shape at ~USD 2/month).

## Ground rules

- The spike app is a **throwaway file-based app outside the repo tree** (`/spike-dotnet`,
  session scratchpad). Nothing in `src/` changes.
- Captured request bodies contain **call transcripts**. Keep them in the scratchpad, quote only
  what is needed into this file, and delete the raw logs afterwards.
- Every answer is recorded under **Findings** with the date and the evidence (log excerpt,
  invoice line, observed behavior). Write "UNVERIFIED" rather than inferring.

## S0 — Setup

- [ ] **S0.1 Telnyx account.** Create the account, complete identity verification, top up about USD 20.
      Record the account-level KYC asked for, and how long it took.
- [ ] **S0.2 Buy a Netherlands number** in Mission Control. Record:
      - the number types offered (geographic / national / mobile / toll-free)
      - the documents required **as an individual**, and whether an address inside the area
        code is needed
      - the time to activation
      - the **monthly price and the inbound per-minute rate as charged** (portal or
        `GET /v2/public/pricing`, not the marketing page)
- [ ] **S0.3 Spike endpoint** (`spike-telnyx.cs`, `#:sdk Microsoft.NET.Sdk.Web`):
      - `POST /v1/chat/completions` appends the **raw headers and body** of every request to a
        JSONL log with a receive timestamp and a per-request sequence number.
      - It answers with an SSE stream (`chat.completion.chunk` deltas, then `data: [DONE]`).
      - It has runtime-switchable modes, chosen by the spoken word the caller says or by a query
        flag in the base URL:
        `echo` (repeat the last user message), `long` (about 60 s of Dutch text, for
        barge-in), `delay:N` (first chunk after N seconds), `filler:N` (one short chunk at
        once, then the rest after N seconds), `nostream` (a single non-SSE JSON response),
        `error` (HTTP 500).
      - It logs `HttpContext.RequestAborted` firing, i.e. Telnyx hanging up the request.
      - Bearer check: compare the header against a local secret and log match/mismatch.
        **Never reject**; the spike needs to see every request.
- [ ] **S0.4 Expose it** with `devtunnel host` (anonymous access); record the HTTPS URL.
- [ ] **S0.5 Create the AI Assistant:**
      - enable **Use Custom LLM**
      - Base URL = the tunnel URL **with a path prefix** (e.g. `…/api/telephony/llm/v1`), to
        prove a prefixed base URL works (see Q8)
      - integration secret = a fresh random 32-byte value
      - language Dutch; pick a Dutch-capable STT model (Parakeet multilingual, or a Deepgram
        multilingual model) and a Dutch TTS voice
      - assign the number from S0.2
      - accept any AI terms Telnyx requires
      - Record every setting, because it becomes the manual-prerequisite list in the new ADR.

## Questions

Each question lists **how** to test, the **pass** condition, and the **kill** condition (the
outcome that sends us to option A).

### Q1 — Request contract (the deciding question)

**How:** place a 3-turn call in `echo` mode and diff the three captured requests.

Record:
- [ ] Is the **full history** sent each turn, or only the new message?
- [ ] Is a **system message** (the assistant's Telnyx-side instructions) included?
- [ ] Is a `tools` array included, and what is in it?
- [ ] The values of `stream` and `model`.
- [ ] The complete `extra_metadata` object. In particular: is there a **stable per-call
      identifier** (call_control_id, call_session_id, conversation id), the **caller's E.164**
      (`telnyx_end_user_target`) and the **called number** (`telnyx_agent_target`)?
- [ ] Every header: exact `Authorization` form, plus any signature, request-id or call-id header.

**Pass:** each request carries both a stable per-call ID and the caller's E.164.
**Kill:** there is no per-call ID **and** no other reliable way to tie turns of one call together.
Deriving one from the message history is fragile and does not count.

### Q2 — Streaming, timeouts and retries (the replay hazard of F5)

**How:** run `delay:2`, `delay:5`, `delay:10`, `delay:20`, `delay:30`, then `nostream`, then `error`.

Record:
- [ ] Is SSE accepted? Is non-streaming accepted?
- [ ] The **timeout** after which Telnyx gives up, and what the caller hears when it does.
- [ ] **Retries:** the number of requests per utterance, by sequence number. Retries replay the
      same turn into a non-reentrant grain and produce the duplicate-turn problem F5 describes.
      Record whether a retry carries an idempotency key or a repeated request ID.
- [ ] What the caller hears on HTTP 500.

**Pass:** the timeout is at least 15 s, **or** `filler:N` (Q6) reliably holds the line.
Retries are either absent or deduplicable from a request field.
**Kill:** the timeout is under about 8 s, a filler chunk does not reset it, and retries cannot be
deduplicated.

### Q3 — Latency

**How:** `echo` mode, 5 utterances. Measure from end of speech to first audio with a stopwatch
or a phone recording, and from request receipt to first chunk from the log.

- [ ] Time to first audio with an immediate first chunk (Telnyx's own overhead).
- [ ] Does TTS start on the **first chunk/sentence**, or only after `[DONE]`?

**Pass:** first audio within about 1.5 s with an instant first chunk, and TTS starts before the
stream ends.

### Q4 — Barge-in and what the history says afterwards

**How:** `long` mode; talk over the reply after about 5 s, then once more mid-sentence.

Record:
- [ ] Does TTS stop? How quickly?
- [ ] Does `RequestAborted` fire on the still-open stream, i.e. does Telnyx cancel the request?
- [ ] Does the **next** request's history hold the **full** assistant message or only the part
      the caller **heard**? This decides whether the "history holds words the caller never
      heard" residual can be fixed on our side (via ADR 0078 stop generation).
- [ ] Do two requests ever overlap? A new request while a stream is still open would hit the
      non-reentrant grain concurrently.

**Pass:** TTS stops within about 1 s. The request is either cancelled, or no overlapping
request arrives before it completes.
**Kill:** overlapping requests arrive with no way to tell which one is current.

### Q5 — Dutch quality

**How:** five Dutch utterances, including a name, a date, a number and a place, and one
English sentence.

- [ ] Transcript accuracy as received in the request; quote the transcripts next to what was
      actually said.
- [ ] How natural the TTS voice sounds. Try at least two Dutch voices if available.
- [ ] Which STT and TTS models were used, and whether they cost more than the base rate.

**Pass:** a listener would accept this for a conversation. Subjective, so record it honestly.

### Q6 — Long turns (Agent42 tool calls run inside one request)

**How:** `filler:15`, then `filler:30`.

- [ ] Is a short first chunk ("Een moment, ik zoek het op.") spoken immediately, with the rest
      spoken when it arrives?
- [ ] Does Telnyx enforce a total-duration limit on one streamed response?
- [ ] If Q1 showed a `tools` array: does Telnyx accept a response with **no** tool calls?
      Agent42 runs its own tools and must never hand tool calls back.

**Pass:** the filler is spoken, and a 30 s turn completes.

### Q7 — Call lifecycle

- [ ] **Caller hang-up:** what arrives at the endpoint (an aborted request? nothing)? Can a
      webhook or status callback report call end? It is needed to close the conversation.
- [ ] **Silence:** the caller says nothing for 30 s, then 60 s. What does the assistant do?
      Record the defaults and whether they can be configured.
- [ ] **Hang-up initiated by us:** is there a built-in hangup tool or another way for the
      endpoint to end the call? It is needed for the "goodbye" path and the F13-style failure path.
- [ ] Is a **welcome greeting** configured on the Telnyx side, or does it come from our endpoint?

### Q8 — Endpoint security (this will be an Easy Auth `excludedPaths` endpoint)

- [ ] Is the bearer token exactly the integration secret, on every request? Is anything else
      signed?
- [ ] Is a base URL with a path prefix accepted (S0.5)? `excludedPaths` matches exactly unless
      the entry ends in `/*` (ADR 0042), so the final route shape must be known.
- [ ] Is the secret per assistant? Expect one assistant per stored agent, so the bearer secret
      would identify the agent.
- [ ] **Design note for the plan, not a test:** do **not** reuse the ADR 0022 agent API key as
      the integration secret. Keep the two credential namespaces apart, which is the
      inbound-email plan's C1 lesson.

### Q9 — Unknown and withheld callers (D2: caller ID only)

**How:** call from an unregistered number, then with caller ID withheld (`#31#` prefix).

- [ ] What `telnyx_end_user_target` contains in each case.
- [ ] What the caller hears when the endpoint refuses (HTTP 403, or a fixed "unknown number"
      reply followed by hang-up).
- [ ] **Are unknown callers billed AI-assistant minutes?** Telnyx probably answers before our
      endpoint is ever asked. If so, record that screening would need a Call Control
      pre-answer hop (option A's mechanism), and what that adds.

**Pass:** withheld and unknown callers are both identifiable and can be refused within a
few seconds.

### Q10 — Real cost

After all calls, read the Telnyx usage/billing detail:

- [ ] number rental (monthly)
- [ ] inbound telephony $/min
- [ ] AI-assistant $/min
- [ ] any STT/TTS premium
- [ ] anything charged per request

Compute the all-in **USD/min, excluding our model tokens**.

**Pass:** at most about USD 0.08/min. Recompute the cost comparison with the measured figures.

### Q11 — Egress and retention (for the ADR 0013 extension)

- [ ] Does the Telnyx portal store **conversation transcripts or recordings** of these calls?
      Is that on by default, can it be turned off, and what is the retention?
- [ ] Where do Telnyx STT and TTS run? Is EU processing available? Record the DPA / subprocessor
      references Telnyx publishes.
- [ ] Record exactly what reaches Telnyx (audio, transcript, our reply text) for the ADR.

No pass/kill condition: this is evidence for the ADR, not a gate. If transcripts **cannot** be
disabled, the user needs to know before approving the plan.

## Decision

| Outcome | Next step |
|---|---|
| Q1, Q2, Q4 pass | Rewrite `plan-phone-call-your-agent.md` around option B via `/plan` (fresh critic pass) |
| Q1 kill (no per-call ID) | Option A: Telnyx Call Control, which keeps the current plan's shape |
| Q2 kill (short timeout, no filler, retries) | Option A, or B restricted to agents without slow tools; user decides |
| Q4 kill (overlapping requests) | Option A |
| Q5 fails on quality only | Try other STT/TTS models first; then option A with Azure Speech |
| Number not obtainable as an individual | Stop; re-evaluate the provider (Twilio +3197 was the fallback) |

## Notes the plan rewrite must pick up regardless of outcome

- **The ADR number in `plan-phone-call-your-agent.md` is stale.** It says "new ADR 0048", but
  0048 is `0048-per-skill-selectable-tools.md` and the highest existing ADR is 0082. The rewrite
  takes the next free number at `/decide` time.
- **F11 needs rewording.** Voice Live was rejected because BYOM allowed no arbitrary endpoint;
  Telnyx's custom LLM does. The argument of D1 ("the agent stays the brain") now favors B.
- **Egress reverses the original D4 rationale.** Audio goes to a third party outside the tenant.
  That is an ADR 0013 extension under CLAUDE.md rule 13 and must be stated as such.

## Findings

*(fill in during the spike — date, evidence, verdict per question)*
