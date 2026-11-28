/* Agent 42 — reading one chat turn's NDJSON stream into its bubble (ADR 0153). Split out of send.js's send() so the POST that
   starts a turn and a reattach to a turn in flight (after a dead socket, an F5, or on another device) share ONE reader and ONE
   event dispatcher. */

import { artifactCardFromToolCall } from "./artifacts.js";
import { closeClientToolDialogs, runClientTool } from "./clienttools.js";
import { cancelAppCards, renderMcpAppCard } from "./mcpapps.js";
import { renderResearchTodos } from "./research.js";
import { applyStateSnapshot } from "./sharedstate.js";
import { addAgentImage, addToolCall, completeToolCall, finishReasoning, finishToolCalls, renderReasoning, renderRouted, renderSuggestions, setAgentMarkdown, showUsage } from "./thread.js";

// What one turn has accumulated so far. Lives across a reattach, so the bubble carries on from where the dead socket left it.
export function newTurn() {
  return {
    answer: "",
    // The provider's own explanation when the turn failed inside a 200 stream (the `error` event). Kept apart from `answer`:
    // that feeds the copy button, and an error is not something the assistant said.
    streamError: null,
    // Set from the `done` event, so it reflects what the server actually did with the turn.
    stopped: false,
    // True once a `done` event was read: the only proof the turn ended cleanly (a stream that just stops proves nothing).
    done: false,
    // The id of the turn (X-Turn-Id) and the last event seq seen: what a reattach asks "after".
    turnId: null,
    lastSeq: 0,
    // toolCallEnd carries no tool name, and an artifact card needs it (read_artifact's result looks the same).
    toolNames: new Map(),
    // The tool input a live MCP App card is fed (ADR 0133); toolCallEnd does not repeat it.
    toolInputs: new Map(),
  };
}

// The id an elicitation is answered under (ADR 0095), from the request JSON the stream line carries.
function elicitationId(elicitation) {
  try {
    return JSON.parse(elicitation ?? "{}").id ?? null;
  } catch {
    return null;
  }
}

// Reads `res` (the POST's response, or a reattach's) to its end and dispatches every event into ctx.agentMsg.
// ctx: { agentMsg, convId,
//   runToolsAbove — client tools and elicitations run only for events with a seq ABOVE this. 0 runs them all (the POST);
//     a reattach passes the X-Turn-Replay-Through value, so replayed events never run twice; Infinity never runs any
//     (a passive attach on another device, ADR 0153 D13),
//   onTextDelta(text) — the reply is spoken as it streams (ADR 0037),
//   onMcpReauth(chunk) — an MCP server's sign-in expired (ADR 0081) }
// Resolves when the stream ends; it throws when the socket dies, which is the caller's cue to reattach.
export async function readTurnResponse(res, turn, ctx) {
  const { agentMsg, convId } = ctx;
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
      // Blank lines are the server's keep-alive.
      if (!line.trim()) continue;
      const chunk = JSON.parse(line);
      // A resume point makes an event seen twice impossible, but a replay is cheap to make idempotent anyway.
      if (typeof chunk.seq === "number") {
        if (chunk.seq <= turn.lastSeq) continue;
        turn.lastSeq = chunk.seq;
      }
      const mayRunTools = typeof chunk.seq !== "number" || chunk.seq > (ctx.runToolsAbove ?? 0);
      dispatch(chunk, turn, ctx, mayRunTools);
    }
  }
}

function dispatch(chunk, turn, ctx, mayRunTools) {
  const { agentMsg, convId } = ctx;
  if (chunk.type === "textDelta") {
    turn.answer += chunk.text;
    setAgentMarkdown(agentMsg, turn.answer, { caret: true });
    ctx.onTextDelta?.(chunk.text); // speak the reply as it streams (ADR 0037); no-op when voice mode is off
  } else if (chunk.type === "routed") {
    // The Auto router picked the model for this turn (ADR 0137).
    renderRouted(agentMsg, chunk.text);
  } else if (chunk.type === "error") {
    turn.streamError = chunk.text || "The model returned an error.";
  } else if (chunk.type === "reasoning") {
    // A reasoning model's thinking (ADR 0071), into a collapsed disclosure above the reply.
    //
    // Deliberately does NOT call onTextDelta, unlike the textDelta arm above: hands-free voice (ADR 0037) would read the
    // model's deliberation aloud before its actual answer, and with a speech model configured (ADR 0038) it would pay a
    // third party per character to do it.
    renderReasoning(agentMsg, chunk.text);
  } else if (chunk.type === "toolCallStart") {
    turn.toolNames.set(chunk.toolCallId, chunk.toolName);
    turn.toolInputs.set(chunk.toolCallId, chunk.toolInput);
    addToolCall(agentMsg, chunk);
    // A tool this page runs (ADR 0089). Not awaited: the reader must keep reading while a dialog is open. Never for a replayed
    // event (the page that sent the turn answers through resumePendingClientTools) or on a passive attach (ADR 0153).
    if (chunk.clientTool && mayRunTools) runClientTool(convId, chunk);
  } else if (chunk.type === "mcpElicitation") {
    // An MCP server asks the user for input mid-tool (ADR 0095): run as the `mcp_elicitation` client tool under the request's
    // own id. Not awaited, for the same reason as a client tool call, and gated the same way.
    if (mayRunTools) {
      runClientTool(convId, { toolCallId: elicitationId(chunk.elicitation), toolName: "mcp_elicitation", toolInput: chunk.elicitation });
    }
  } else if (chunk.type === "toolCallEnd") {
    completeToolCall(agentMsg, chunk);
    artifactCardFromToolCall(agentMsg, convId, turn.toolNames.get(chunk.toolCallId), chunk.toolResult);
    // An MCP App tool (ADR 0133): the view and the tool's raw result, live and active at once.
    if (chunk.mcpApp) {
      renderMcpAppCard(agentMsg, convId, {
        toolCallId: chunk.toolCallId, toolInput: turn.toolInputs.get(chunk.toolCallId),
        app: chunk.mcpApp.app, resultJson: chunk.mcpApp.resultJson,
      }, { live: true });
    }
  } else if (chunk.type === "image") {
    // An image-generation model produced an image (ADR 0020): render it live in the assistant bubble via the display proxy,
    // exactly like it will re-render on reload.
    addAgentImage(agentMsg, convId, chunk.attachmentId);
  } else if (chunk.type === "mcpReauthRequired") {
    ctx.onMcpReauth?.(chunk);
  } else if (chunk.type === "todos") {
    // The research agent's checklist changed (ADR 0082): the whole list, so render replaces.
    renderResearchTodos(agentMsg, chunk.todos);
  } else if (chunk.type === "stateSnapshot") {
    // The shared state changed (ADR 0089): the whole state, so the panel replaces rather than patches.
    applyStateSnapshot(convId, chunk.state, chunk.stateVersion);
  } else if (chunk.type === "textReset") {
    // A later research iteration started its answer (ADR 0082): the text so far was a draft. Clear it and keep the tool calls,
    // so the bubble ends on what a reload will show.
    turn.answer = "";
    setAgentMarkdown(agentMsg, turn.answer, { caret: true });
  } else if (chunk.type === "suggestions") {
    // Up to 3 clickable follow-up prompts under the reply; clicking one sends it immediately.
    renderSuggestions(agentMsg, chunk.suggestions);
  } else if (chunk.type === "done") {
    // Settled here as well as in the caller's finally: the disclosures should stop pulsing — and a long tool chain fold
    // away — the moment the turn reports done, not one microtask later when the reader drains.
    turn.done = true;
    finishReasoning(agentMsg);
    finishToolCalls(agentMsg);
    // The turn is over, so a confirm still open (ADR 0089) answers a call nobody waits for any more. Here and on a recovered
    // reply only, never in the finally: after a failed recovery the server may still be waiting on that very dialog.
    closeClientToolDialogs();
    showUsage(agentMsg, chunk);
    // The SERVER decides whether this turn counts as stopped (ADR 0078), not the click: a press that lands as the last token
    // goes out stopped nothing, and labelling that turn would be a lie.
    if (chunk.stopped) {
      turn.stopped = true;
      cancelAppCards();
    }
  }
}
