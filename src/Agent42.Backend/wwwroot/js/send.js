/* Agent 42 — sending & streaming. Split out of app.js (ADR 0041). */

import { ephemeral } from "./agents.js";
import { artifactCardFromToolCall, renderArtifactCards } from "./artifacts.js";
import { clearPendingAttachments, pendingAttachments } from "./attachments.js";
import { cachePut } from "./cache.js";
import { autosize } from "./composer.js";
import { activeAgentId, activeId, pendingAgentId, setPendingAgentId } from "./conversations.js";
import { form, incognitoBtn, input, modelSelect, stopBtn, thread } from "./dom.js";
import { currentLocationForSend } from "./location.js";
import { renderResearchTodos } from "./research.js";
import { applyModelAvailability } from "./models.js";
import { setTopbarShare } from "./sharelinks.js";
import { conversations, refreshConversations } from "./sidebar.js";
import { addAgentImage, addCopyButton, addMessage, addToolCall, clearSuggestions, completeToolCall, fillUserBubble, finishReasoning, finishToolCalls, markStopped, renderMcpReauth, renderReasoning, renderSuggestions, replayToolCalls, scrollToBottom, setAgentMarkdown, showUsage } from "./thread.js";

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

export async function send(prompt) {
  stopSpeech(); // capture the dictated prompt and reset the mic before clearing the box
  streaming = true;
  modelSelect.disabled = true;
  // The stop control exists only while there is something to stop. Revealed here rather than on the first
  // delta so it covers the wait before the first token — which is exactly when a user reaches for it.
  stopBtn.hidden = false;
  // Ephemerality is fixed at creation (ADR 0034): once the first prompt goes out, lock the toggle
  // for the life of this chat. "New chat" re-enables it.
  incognitoBtn.disabled = true;
  input.value = "";
  autosize();

  // Snapshot the composed attachments for this turn and clear the strip so the next turn
  // starts clean. They were uploaded to activeId, so they belong to this conversation.
  const turnAttachments = pendingAttachments;
  const convId = activeId;
  clearPendingAttachments();

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

  let answer = "";
  // Set from the `done` event, so it reflects what the server actually did with the turn.
  let stopped = false;
  // toolCallEnd carries no tool name, and an artifact card needs it (read_artifact's result looks the same).
  const toolNames = new Map();
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
        // Whatever fix is already cached (ADR 0012) — read synchronously, never awaited. No
        // geolocation outcome (denied, timed out, unsupported, stale, absent) may delay a chat.
        location: currentLocationForSend(),
        // Incognito (ADR 0034): the server skips this turn's session write and never lists the
        // conversation, so it leaves no trace. Also keeps it out of the offline cache — an ephemeral
        // chat is never TouchAsync'd, so refreshConversations()/cachePut never see it.
        ephemeral,
      }),
    });
    if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });

      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";
      for (const line of lines) {
        if (!line.trim()) continue;
        const chunk = JSON.parse(line);
        if (chunk.type === "textDelta") {
          answer += chunk.text;
          setAgentMarkdown(agentMsg, answer, { caret: true });
          voice.onDelta(chunk.text); // speak the reply as it streams (ADR 0037); no-op when voice mode is off
        } else if (chunk.type === "reasoning") {
          // A reasoning model's thinking (ADR 0071), into a collapsed disclosure above the reply.
          //
          // Deliberately does NOT call voice.onDelta, unlike the textDelta arm above: hands-free voice
          // (ADR 0037) would read the model's deliberation aloud before its actual answer, and with a
          // speech model configured (ADR 0038) it would pay a third party per character to do it.
          renderReasoning(agentMsg, chunk.text);
        } else if (chunk.type === "toolCallStart") {
          toolNames.set(chunk.toolCallId, chunk.toolName);
          addToolCall(agentMsg, chunk);
        } else if (chunk.type === "toolCallEnd") {
          completeToolCall(agentMsg, chunk);
          artifactCardFromToolCall(agentMsg, convId, toolNames.get(chunk.toolCallId), chunk.toolResult);
        } else if (chunk.type === "image") {
          // An image-generation model produced an image (ADR 0020): render it live in the assistant
          // bubble via the display proxy, exactly like it will re-render on reload.
          addAgentImage(agentMsg, activeId, chunk.attachmentId);
        } else if (chunk.type === "mcpReauthRequired") {
          // An MCP server's sign-in expired (ADR 0081): a Reconnect button in the bubble, opening a new tab.
          renderMcpReauth(agentMsg, chunk.mcpServerId, chunk.text);
        } else if (chunk.type === "todos") {
          // The research agent's checklist changed (ADR 0082): the whole list, so render replaces.
          renderResearchTodos(agentMsg, chunk.todos);
        } else if (chunk.type === "textReset") {
          // A later research iteration started its answer (ADR 0082): the text so far was a draft. Clear it and
          // keep the tool calls, so the bubble ends on what a reload will show.
          answer = "";
          setAgentMarkdown(agentMsg, answer, { caret: true });
        } else if (chunk.type === "suggestions") {
          // Up to 3 clickable follow-up prompts under the reply; clicking one sends it immediately.
          renderSuggestions(agentMsg, chunk.suggestions);
        } else if (chunk.type === "done") {
          // Settled here as well as in the finally: the disclosures should stop pulsing — and a long
          // tool chain fold away — the moment the turn reports done, not one microtask later when the
          // reader drains.
          finishReasoning(agentMsg);
          finishToolCalls(agentMsg);
          showUsage(agentMsg, chunk);
          // The SERVER decides whether this turn counts as stopped (ADR 0078), not the click: a press that
          // lands as the last token goes out stopped nothing, and labelling that turn would be a lie.
          if (chunk.stopped) stopped = true;
        }
      }
    }
    setAgentMarkdown(agentMsg, answer);
    // Live-streamed reply is final: capture the raw markdown accumulator by closure for copy.
    addCopyButton(agentMsg, answer);
    // After the copy button, so the note it appends cannot be captured into the clipboard text. A turn
    // stopped before its first token loses this bubble entirely — see markStopped.
    if (stopped) markStopped(agentMsg, answer.length > 0);
    voice.onStreamEnd(); // speak any buffered tail (ADR 0037); no-op when voice mode is off
    // The persona is now bound server-side; don't resend agentId on later turns. Kept on
    // failure so a retry rebinds.
    setPendingAgentId(null);
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
  } catch (err) {
    // Barge-in (ADR 0037): an aborted turn is a deliberate interruption, not a failure —
    // leave the partial reply as-is, don't paint the error state. voice mode drives what
    // happens next (it speaks the tail / captures the interrupting utterance).
    if (err.name === "AbortError") {
      setAgentMarkdown(agentMsg, answer);
      voice.onStreamEnd();
    } else if (await recoverPersistedReply(agentMsg, convId, priorTurns, backgrounded)) {
      // The stream died but the turn did not. Finish exactly as the success path does — the reply
      // IS on the server, so the persona is bound and the conversation is shareable.
      voice.onStreamEnd();
      setPendingAgentId(null);
      setTopbarShare(convId, conversations.find((c) => c.id === convId)?.title ?? "This conversation");
    } else {
      agentMsg.classList.add("is-error");
      setAgentMarkdown(agentMsg, `**Transmission failed.** ${err.message ?? err}`);
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
    modelSelect.disabled = modelSelect.options.length === 0;
    applyModelAvailability(); // keep the composer gated if there's still no model (ADR 0019)
    input.focus();
    refreshConversations();
  }
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
