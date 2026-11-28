/* Agent 42 — sending & streaming. Split out of app.js (ADR 0041). */

import { ephemeral } from "./agents.js";
import { renderArtifactCards } from "./artifacts.js";
import { newTurn, readTurnResponse } from "./chatstream.js";
import { clearPendingAttachments, pendingAttachments } from "./attachments.js";
import { CLIENT_TOOLS, closeClientToolDialogs, resumePendingClientTools } from "./clienttools.js";
import { cachePut } from "./cache.js";
import { autosize } from "./composer.js";
import { activeAgentId, activeId, pendingAgentId, setPendingAgentId } from "./conversations.js";
import { form, incognitoBtn, input, modelSelect, stopBtn, thread } from "./dom.js";
import { syncMessageActions } from "./editfork.js";
import { getAppContext, renderMcpAppCards } from "./mcpapps.js";
import { watchMcpReconnect } from "./mcp.js";
import { applyModelAvailability } from "./models.js";
import { setTopbarShare } from "./sharelinks.js";
import { conversations, refreshConversations } from "./sidebar.js";
import { addAgentImage, addCopyButton, addMessage, clearSuggestions, fillUserBubble, finishReasoning, finishToolCalls, markStopped, renderMcpReauth, replayToolCalls, scrollToBottom, setAgentMarkdown } from "./thread.js";

export let streaming = false;
// Stops live speech dictation. Reassigned via setStopSpeech() by wireSpeech() in speech.js when
// the Web Speech API is available; stays a no-op when it isn't (e.g. Firefox). Called at the top
// of send() so a late recognition result can't repopulate the just-cleared textarea.
// This slot IS the seam that breaks the send ↔ speech/voice cycle (ADR 0041) — do not “clean it up”
// into a direct import.
export let stopSpeech = () => {};

// Aborts the in-flight chat fetch. Set for the life of a send() (its AbortController),
// null otherwise. Barge-in (ADR 0037) and teardown call sendAbort?.abort() to stop
// generation mid-stream; an AbortError is treated as a clean interruption, not a failure.
export let sendAbort = null;

// Hands-free voice conversation controller (ADR 0037). Reassigned via setVoice() by wireVoice() in
// voice.js when SpeechRecognition + speechSynthesis are both available; stays inert otherwise. send()
// feeds it stream deltas so the reply can be spoken sentence-by-sentence as it arrives. Same seam as
// stopSpeech above — send.js only ever READS it, at call time (ADR 0041).
export let voice = { active: false, onDelta: () => {}, onStreamEnd: () => {}, stop: () => {} };

// The newest turn's watch for an MCP sign-in to finish (ADR 0087), or null. One at a time: send()
// cancels it, so only a turn that is still the user's latest can ever be re-sent.
let reconnectWatch = null;
// A re-send whose sign-in finished while its own turn was still streaming (the pre-turn notice comes first, so
// that is reachable). Run by that turn's finally.
let deferredResend = null;

// Re-sends a turn whose MCP server needed a fresh sign-in, once the user has done it — the reply ran without that
// server's tools. The failed turn stays in the history (undoing it would mean rewriting the session); the model
// simply sees the same prompt again, now with the tools. Re-sent only while the turn is still the newest one, in
// the conversation that is still open; otherwise the note says the tools are back instead.
function resendAfterReconnect(prompt, attachments, convId, agentMsg) {
  const watch = watchMcpReconnect(function resend() {
    // Any newer send() cancels this watch, so a stream running now is this very turn: wait for it to end.
    if (streaming && reconnectWatch === watch) {
      deferredResend = resend;
      return;
    }
    const block = agentMsg.querySelector(".msg__reauth");
    block?.querySelector(".msg__reauth-btn")?.remove();
    const note = block?.querySelector(".msg__reauth-text");
    if (reconnectWatch !== watch || activeId !== convId || !agentMsg.isConnected) {
      if (note) note.textContent = "Reconnected. Send your message again to use it.";
      return;
    }
    if (note) note.textContent = "Reconnected. Sending your message again…";
    send(prompt, { attachments });
  });
  reconnectWatch = watch;
  return watch;
}

// How long recoverPersistedReply() waits for a backgrounded turn to show up in the history.
// Roughly half a minute, which covers a tool-calling turn that was still running when the app
// came back; a turn slower than that is left to the sidebar and a later reopen.
const RECOVER_ATTEMPTS = 15;
const RECOVER_DELAY_MS = 2000;

// Adopts the reply the server already persisted, after the response stream died under us.
//
// This is the backgrounded-PWA case, and the failure is a lie: Android freezes an installed PWA
// when the user switches apps and the response socket goes with it, so reader.read() rejects the
// instant the app is resumed — while the SERVER ran the turn to completion and wrote its session,
// oblivious (ChatStreamDisconnectTests pins that: a turn does not stop when its client does). That
// is why a refresh shows the reply. So re-read the conversation and paint the real answer instead
// of "Transmission failed" over a turn that succeeded.
//
// Polling, because resuming can beat the turn: the history only gains the assistant message once
// the run finishes, so the first look may legitimately come up short.
async function recoverPersistedReply(agentMsg, convId, priorTurns, backgrounded) {
  // An incognito chat is never written at all (ADR 0034) — there is nothing to recover, ever.
  if (ephemeral) return false;
  // A turn that never went to the background gets ONE cheap look — a transient blip can still leave
  // a completed turn behind — and then fails honestly. Polling a genuinely broken connection for
  // half a minute would only hide the error behind a caret that is never going to stop blinking.
  const attempts = backgrounded ? RECOVER_ATTEMPTS : 1;
  for (let attempt = 0; attempt < attempts; attempt++) {
    if (attempt > 0) await new Promise((resolve) => setTimeout(resolve, RECOVER_DELAY_MS));

    // A turn still running may be waiting on THIS page for a client tool (ADR 0089): a confirm the frozen PWA never
    // showed. Answer it, or the turn waits out its timeout and the history below never completes in time.
    resumePendingClientTools(convId);

    let turns = null;
    try {
      const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/messages`);
      if (res.ok) turns = await res.json();
    } catch {
      // Still no network. Fall through and try again on the next attempt.
    }

    // Rendered .msg elements are 1:1 with history turns (renderTurns builds one per turn), so the
    // server is holding THIS turn once the history has grown by both of its messages — the user's
    // and the reply — and ends on the reply.
    const last = turns?.[turns.length - 1];
    if (turns && turns.length >= priorTurns + 2 && last.role !== "user") {
      setAgentMarkdown(agentMsg, last.text ?? "");
      addCopyButton(agentMsg, last.text ?? "");
      // An assistant turn may carry a generated image (ADR 0020) — the live `image` event for it
      // went down with the stream, so re-render from the history exactly as a reload would.
      for (const att of last.attachments ?? []) addAgentImage(agentMsg, convId, att.id, att.fileName);
      // Same for the tool calls (ADR 0080). This bubble may already hold the chips that arrived BEFORE
      // the disconnect, which is why the shared helper de-dupes by call id rather than appending — and
      // why a chip the disconnect left pulsing gets healed here rather than staying stuck.
      replayToolCalls(agentMsg, last.toolCalls);
      // ...and the artifact cards, de-duped per artifact version like the chips.
      renderArtifactCards(agentMsg, convId, last.artifacts);
      renderMcpAppCards(agentMsg, convId, last.toolCalls);
      cachePut("messages:" + convId, turns); // keep the offline mirror in step (ADR 0032)
      return true;
    }
  }

  return false;
}

// Stops the turn on the SERVER (ADR 0078), which is the whole point: aborting the fetch alone leaves the
// model running, the tokens spent and the full reply persisted — so the answer you "stopped" is what a
// reload shows. The server cuts the run short and keeps what it had already streamed, and the stream then
// ends with a normal `done` carrying `stopped`, so nothing here has to abort on the happy path.
//
// `abortLocally` is for voice barge-in (ADR 0037), which needs the deltas to stop arriving THIS instant —
// its onDelta speaks whatever it is handed, so a round trip's worth of buffered text would still be read
// aloud over the user. On the button path the abort is only a fallback for a stop call that never landed,
// so the control always does something visible.
export async function stopGeneration({ abortLocally = false } = {}) {
  const convId = activeId;
  // BEFORE the fetch, and the order is the whole point: `await` on the stop request is a full round trip,
  // and onDelta keeps speaking every delta that arrives during it. Aborting afterwards would make voice
  // barge-in stop-only in practice — the server has already cancelled by the time the abort runs — which
  // is exactly the ADR 0037 property this is here to preserve.
  if (abortLocally) sendAbort?.abort();
  try {
    const res = await fetch(`/api/chat/${encodeURIComponent(convId)}/stop`, { method: "POST" });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  } catch {
    // Offline, or the request failed. Fall back to the old client-only interruption: the reply on screen
    // stops growing, which is what the user asked for, even though the server keeps going.
    sendAbort?.abort();
  }
}

// `replay` re-sends an earlier turn (resendAfterReconnect): its attachments, which are already uploaded to this
// conversation, and the composer left alone — the user may be typing their next message there. An edit or
// regenerate (editfork.js, ADR 0092) is a replay with `replaceLastTurn` and the concurrency token: the server
// removes the last exchange before this turn runs, and if the turn then fails the text goes back in the composer.
export async function send(prompt, replay = null) {
  reconnectWatch?.cancel(); // a newer turn: the previous one is no longer worth re-sending
  reconnectWatch = null;
  deferredResend = null;
  if (!replay) stopSpeech(); // capture the dictated prompt and reset the mic before clearing the box
  streaming = true;
  syncMessageActions(); // no edit, regenerate or fork while a turn streams
  modelSelect.disabled = true;
  // The stop control exists only while there is something to stop. Revealed here rather than on the first
  // delta so it covers the wait before the first token — which is exactly when a user reaches for it.
  stopBtn.hidden = false;
  // Ephemerality is fixed at creation (ADR 0034): once the first prompt goes out, lock the toggle
  // for the life of this chat. "New chat" re-enables it.
  incognitoBtn.disabled = true;
  if (!replay) {
    input.value = "";
    autosize();
  }

  // Snapshot the composed attachments for this turn and clear the strip so the next turn
  // starts clean. They were uploaded to activeId, so they belong to this conversation.
  const turnAttachments = replay ? replay.attachments : pendingAttachments;
  const convId = activeId;
  if (!replay) clearPendingAttachments();
  // Created by this turn's first mcpReauthRequired event, if it has one.
  let turnWatch = null;

  // Follow-up suggestion chips are live-only and belong to the newest turn: drop any left over from
  // the previous reply before this turn appends its own.
  clearSuggestions();

  // How many turns are on screen BEFORE this one appends its two bubbles — the yardstick
  // recoverPersistedReply() measures the server's history against.
  const priorTurns = thread.querySelectorAll(".msg").length;
  // Whether this turn spent any time in the background. It has to be latched by a listener:
  // a frozen PWA runs no JS, so the stream's rejection is not observed until the app is already
  // visible again and reading document.hidden in the catch would always say "no".
  let backgrounded = document.hidden;
  const onVisibilityChange = () => { backgrounded = backgrounded || document.hidden; };
  document.addEventListener("visibilitychange", onVisibilityChange);

  const userMsg = addMessage("user");
  fillUserBubble(userMsg, prompt, turnAttachments, convId);
  const agentMsg = addMessage("agent");
  setAgentMarkdown(agentMsg, "", { caret: true });
  // The only scroll of the turn: bring the freshly appended turn into view. The reply is then
  // deliberately NOT followed as it streams — the view stays wherever the reader put it, so
  // scrolling back to re-read something earlier is not yanked away by the next chunk.
  scrollToBottom();

  const turn = newTurn();
  const ctx = {
    agentMsg, convId,
    runToolsAbove: 0,
    onTextDelta: (text) => voice.onDelta(text),
    // One watch for the whole turn, so two servers needing a sign-in re-send it once, after both are back.
    onMcpReauth: (chunk) => {
      turnWatch ??= resendAfterReconnect(prompt, turnAttachments, convId, agentMsg);
      renderMcpReauth(agentMsg, chunk.mcpServerId, chunk.text, turnWatch);
    },
  };
  // The success tail, shared by a stream that ran to its end and one that was rejoined after the socket died (ADR 0153).
  const finishLiveReply = () => {
    setAgentMarkdown(agentMsg, turn.answer);
    // After the reply, so a turn that said something and then failed keeps what it said and shows why it stopped.
    if (turn.streamError) {
      agentMsg.classList.add("is-error");
      setAgentMarkdown(agentMsg, turn.answer ? `${turn.answer}

  **The model reported an error.** ${turn.streamError}` : `**The model reported an error.** ${turn.streamError}`);
    }
    // Live-streamed reply is final: capture the raw markdown accumulator by closure for copy.
    addCopyButton(agentMsg, turn.answer);
    // After the copy button, so the note it appends cannot be captured into the clipboard text. A turn
    // stopped before its first token loses this bubble entirely — see markStopped.
    if (turn.stopped) markStopped(agentMsg, turn.answer.length > 0);
    voice.onStreamEnd(); // speak any buffered tail (ADR 0037); no-op when voice mode is off
    // The persona is now bound server-side; don't resend agentId on later turns. Kept on
    // failure so a retry rebinds. Kept while incognito too (ADR 0134): an incognito binding is never stored, so a
    // grain that deactivates between turns would otherwise run the next one as an unbound plain chat.
    if (!ephemeral) setPendingAgentId(null);
    // This turn persisted, so the conversation now has something to share — unless it is incognito,
    // which is never written at all (ADR 0034). Deliberately here at the END of the try and not beside
    // the incognitoBtn lock at the top of send(): that line runs BEFORE the fetch, so an offline send, a
    // failed turn or a voice barge-in would light the button up on a chat that persisted nothing.
    // Failure therefore leaves it disabled — the conservative direction, and self-healing: the chat
    // appears in the sidebar and opening it enables share through selectConversation.
    // The title lookup is the same one selectConversation does: a chat that already has a sidebar row
    // has a real title, and only a brand-new one (whose title the server derives after this turn) falls
    // back to the constant. Passing the constant unconditionally would blank the title of every
    // already-named chat on its second turn.
    if (!ephemeral) setTopbarShare(convId, conversations.find((c) => c.id === convId)?.title ?? "This conversation");
  };
  try {
    // In an agent chat the model is pinned server-side, so never send a model (it would
    // switch the conversation off the agent's model). agentId is sent only on the first
    // turn of a freshly started agent chat (pendingAgentId), never on resumes.
    const isAgentChat = activeAgentId != null;
    // Barge-in (ADR 0037): an AbortController lets voice mode (or teardown) stop generation
    // mid-stream the instant the user talks over the reply.
    sendAbort = new AbortController();
    const res = await fetch(`/api/chat/${encodeURIComponent(activeId)}`, {
      method: "POST",
      signal: sendAbort.signal,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        message: prompt,
        model: isAgentChat ? null : (modelSelect.value || null),
        agentId: pendingAgentId,
        attachmentIds: turnAttachments.map((a) => a.id),
        // Incognito (ADR 0034): the server skips this turn's session write and never lists the
        // conversation, so it leaves no trace. Also keeps it out of the offline cache — an ephemeral
        // chat is never TouchAsync'd, so refreshConversations()/cachePut never see it.
        ephemeral,
        // The client tools this page can run (ADR 0089). The server defines them and intersects this list with its
        // own; a call to one the page did not list answers "unavailable" without reaching it.
        clientTools: CLIENT_TOOLS,
        // What the open MCP App views asked the assistant to know (ADR 0133), or null. Untrusted; used for this turn only.
        appContext: getAppContext(convId),
        // Edit and regenerate (ADR 0092): replace the last exchange, if the history is still the one read.
        replaceLastTurn: replay?.replaceLastTurn ?? false,
        expectedUserTurnCount: replay?.expectedUserTurnCount ?? null,
        expectedLastUserTextHash: replay?.expectedLastUserTextHash ?? null,
      }),
    });
    if (!res.ok || !res.body) throw await httpError(res);

    turn.turnId = res.headers.get("X-Turn-Id");
    await readTurnResponse(res, turn, ctx);
    finishLiveReply();
  } catch (err) {
    // Barge-in (ADR 0037): an aborted turn is a deliberate interruption, not a failure —
    // leave the partial reply as-is, don't paint the error state. voice mode drives what
    // happens next (it speaks the tail / captures the interrupting utterance).
    if (err.name === "AbortError") {
      setAgentMarkdown(agentMsg, turn.answer);
      voice.onStreamEnd();
    } else if (!err.status && turn.turnId && await reattachTurn(turn, ctx, convId)) {
      // The socket died mid-reply but the turn is still ours to read: the server replayed what this page missed and the reply
      // ran to its `done` (ADR 0153). Finish exactly as a stream that never broke.
      finishLiveReply();
    } else if (!(err.status && replay?.replaceLastTurn) && await recoverPersistedReply(agentMsg, convId, priorTurns, backgrounded)) {
      // The stream died but the turn did not. Finish exactly as the success path does — the reply
      // IS on the server, so the persona is bound and the conversation is shareable.
      closeClientToolDialogs();
      voice.onStreamEnd();
      setPendingAgentId(null);
      setTopbarShare(convId, conversations.find((c) => c.id === convId)?.title ?? "This conversation");
    } else {
      agentMsg.classList.add("is-error");
      // A refused edit or regenerate (409) is not a transmission failure: the server says why, and changed nothing.
      setAgentMarkdown(agentMsg, err.status === 409 ? err.message : `**Transmission failed.** ${err.message ?? err}`);
      // An edit or regenerate that did not produce a reply: the old exchange may already be gone (ADR 0092), so
      // the text goes back where the user can send it again.
      if (replay?.replaceLastTurn && !input.value) {
        input.value = prompt;
        autosize();
      }
    }
  } finally {
    // Settle the reasoning disclosure here, not in each tail (ADR 0071). `done` closes it on the happy
    // path, but there are three others that never see a `done`: a voice barge-in (AbortError, ADR 0037),
    // a stream that died and was recovered (ADR 0046), and an outright failure. Each would otherwise
    // leave a pulsing "Thinking…" above a reply that is finished, interrupted or errored — and nothing
    // heals it, since reasoning is never persisted and so never replayed. A no-op on a turn that never
    // reasoned, and idempotent, so the `done` arm calling it first costs nothing.
    finishReasoning(agentMsg);
    // The tool group settles on exactly the same four tails and for nearly the same reason: an interrupted
    // or failed turn would otherwise be left with a pulsing lamp over an expanded chain of chips. Since
    // ADR 0080 a reload DOES heal it — but only a reload, and this is what settles it in the session the
    // user is actually sitting in.
    finishToolCalls(agentMsg);
    document.removeEventListener("visibilitychange", onVisibilityChange);
    sendAbort = null;
    stopBtn.hidden = true;
    streaming = false;
    syncMessageActions();
    modelSelect.disabled = modelSelect.options.length === 0;
    applyModelAvailability(); // keep the composer gated if there's still no model (ADR 0019)
    input.focus();
    refreshConversations();
    // Last, once `streaming` is false: a sign-in that finished mid-turn re-sends now (resendAfterReconnect).
    const next = deferredResend;
    deferredResend = null;
    next?.();
  }
}

// Rejoins a turn whose response socket died (ADR 0153): asks the server for everything after the last event this page saw and
// reads it into the same bubble. True only when the turn then ran to its `done`; everything else (404, a turn that failed, was
// interrupted or was cut short, a server without this feature) returns false, and the caller falls back to polling the history.
const REATTACH_DELAYS_MS = [500, 1000, 2000, 3000, 5000];
async function reattachTurn(turn, ctx, convId) {
  for (const delay of REATTACH_DELAYS_MS) {
    await new Promise((resolve) => setTimeout(resolve, delay));
    try {
      const url = `/api/chat/${encodeURIComponent(convId)}/turns/${encodeURIComponent(turn.turnId)}/stream`
        + `?after=${turn.lastSeq}&ephemeral=${ephemeral ? "true" : "false"}`;
      const res = await fetch(url, { signal: sendAbort?.signal });
      // Gone, unavailable or refused: the server answered, so retrying will not change it.
      if (!res.ok || !res.body) return false;
      // Events up to here were history when this page attached; only newer client-tool calls are this page's to run.
      ctx.runToolsAbove = Number(res.headers.get("X-Turn-Replay-Through")) || 0;
      // A confirm the dead socket never showed, if the turn is still waiting for it.
      resumePendingClientTools(convId);
      await readTurnResponse(res, turn, ctx);
      return turn.done;
    } catch (err) {
      // A barge-in or stop that aborted the fetch: nothing more to rejoin.
      if (err.name === "AbortError") return false;
      // The socket died again (a network that is still down): wait and ask again from the same point.
    }
  }
  return false;
}

// The error for a non-OK chat response, carrying the server's own message when it sent one (a 409 from an edit or
// regenerate explains itself) and the status, which tells the catch the server answered rather than went away.
async function httpError(res) {
  const body = await res.json().catch(() => null);
  const err = new Error(body?.error ?? `HTTP ${res.status}`);
  err.status = res.status;
  return err;
}

export function wireSend() {
  form.addEventListener("submit", (e) => {
    e.preventDefault();
    const prompt = input.value.trim();
    // Allow an image-only turn (no text) as long as something is attached.
    if ((!prompt && pendingAttachments.length === 0) || streaming) return;
    send(prompt);
  });

  // The button sits inside the composer form, and what keeps a press from submitting it is `type="button"`
  // in the markup — not anything here. This listener only asks the server to stop.
  //
  // No abort on this path: the stream ends by itself with a `done` carrying `stopped`, which is what marks
  // the bubble. The fallback inside stopGeneration does abort, and then the AbortError tail runs instead —
  // so on a failed stop call the reply freezes with no "Stopped." mark, which is the honest outcome (the
  // server did not stop).
  stopBtn.addEventListener("click", () => stopGeneration());
}

export function setStopSpeech(value) {
  stopSpeech = value;
}

export function setVoice(value) {
  voice = value;
}

// Joins a turn that is running on this conversation, started elsewhere (another device, or this page before an F5): ADR 0153.
// PASSIVE, on purpose (D13): events are rendered but no client tool or elicitation ever runs here, so a second device cannot
// double-prompt or repeat a side effect. The grain's history does not hold a turn in flight (not even its user message), so the
// bubble is the assistant's alone, and when the turn ends the conversation is read again and repainted by `onFinished`.
// Stop works from here unchanged (a grain call). Returns once the turn has ended or the page navigated away.
let passiveAbort = null;
export function isPassiveAttach() { return passiveAbort !== null; }
export function detachPassive() { passiveAbort?.abort(); }

export async function attachToRunningTurn(convId, turnId, onFinished) {
  if (streaming) return;
  streaming = true;
  passiveAbort = new AbortController();
  syncMessageActions();
  modelSelect.disabled = true;
  stopBtn.hidden = false;
  incognitoBtn.disabled = true;

  const agentMsg = addMessage("agent");
  setAgentMarkdown(agentMsg, "", { caret: true });
  const turn = newTurn();
  turn.turnId = turnId;
  const ctx = { agentMsg, convId, runToolsAbove: Infinity };
  let finished = false;
  try {
    const res = await fetch(
      `/api/chat/${encodeURIComponent(convId)}/turns/${encodeURIComponent(turnId)}/stream?after=0&ephemeral=false`,
      { signal: passiveAbort.signal });
    if (!res.ok || !res.body) return;
    await readTurnResponse(res, turn, ctx);
    finished = true;
  } catch {
    // Aborted by navigating away, or the socket died: history owns the outcome either way.
  } finally {
    finishReasoning(agentMsg);
    finishToolCalls(agentMsg);
    const aborted = passiveAbort?.signal.aborted ?? true;
    passiveAbort = null;
    stopBtn.hidden = true;
    streaming = false;
    syncMessageActions();
    modelSelect.disabled = modelSelect.options.length === 0;
    applyModelAvailability();
    // Still looking at this conversation: repaint it from the history, which now holds the whole turn (one bubble, not two).
    if (!aborted && activeId === convId) {
      agentMsg.remove();
      await onFinished?.(finished);
    }
    refreshConversations();
  }
}
