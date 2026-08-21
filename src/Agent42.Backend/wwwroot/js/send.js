/* Agent 42 — sending & streaming. Split out of app.js (ADR 0041). */

import { ephemeral } from "./agents.js";
import { clearPendingAttachments, pendingAttachments } from "./attachments.js";
import { autosize } from "./composer.js";
import { activeAgentId, activeId, pendingAgentId, setPendingAgentId } from "./conversations.js";
import { form, incognitoBtn, input, modelSelect } from "./dom.js";
import { currentLocationForSend } from "./location.js";
import { applyModelAvailability } from "./models.js";
import { setTopbarShare } from "./sharelinks.js";
import { conversations, refreshConversations } from "./sidebar.js";
import { addAgentImage, addCopyButton, addMessage, addToolCall, clearSuggestions, completeToolCall, fillUserBubble, renderSuggestions, scrollToBottom, setAgentMarkdown, showUsage } from "./thread.js";

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
export async function send(prompt) {
  stopSpeech(); // capture the dictated prompt and reset the mic before clearing the box
  streaming = true;
  modelSelect.disabled = true;
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

  const userMsg = addMessage("user");
  fillUserBubble(userMsg, prompt, turnAttachments, convId);
  const agentMsg = addMessage("agent");
  setAgentMarkdown(agentMsg, "", { caret: true });
  scrollToBottom();

  let answer = "";
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
        } else if (chunk.type === "toolCallStart") {
          addToolCall(agentMsg, chunk);
        } else if (chunk.type === "toolCallEnd") {
          completeToolCall(agentMsg, chunk);
        } else if (chunk.type === "image") {
          // An image-generation model produced an image (ADR 0020): render it live in the assistant
          // bubble via the display proxy, exactly like it will re-render on reload.
          addAgentImage(agentMsg, activeId, chunk.attachmentId);
        } else if (chunk.type === "suggestions") {
          // Up to 3 clickable follow-up prompts under the reply; clicking one sends it immediately.
          renderSuggestions(agentMsg, chunk.suggestions);
        } else if (chunk.type === "done") {
          showUsage(agentMsg, chunk);
        }
        scrollToBottom();
      }
    }
    setAgentMarkdown(agentMsg, answer);
    // Live-streamed reply is final: capture the raw markdown accumulator by closure for copy.
    addCopyButton(agentMsg, answer);
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
    } else {
      agentMsg.classList.add("is-error");
      setAgentMarkdown(agentMsg, `**Transmission failed.** ${err.message ?? err}`);
    }
  } finally {
    sendAbort = null;
    streaming = false;
    modelSelect.disabled = modelSelect.options.length === 0;
    applyModelAvailability(); // keep the composer gated if there's still no model (ADR 0019)
    scrollToBottom();
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
}

export function setStopSpeech(value) {
  stopSpeech = value;
}

export function setVoice(value) {
  voice = value;
}
