package nl.agent42.android.chat

import nl.agent42.android.api.AttachmentRef
import nl.agent42.android.api.ChatTurn
import nl.agent42.android.api.ResearchTodo
import nl.agent42.android.api.StreamEvent
import nl.agent42.android.api.StreamEventType

enum class Role { USER, ASSISTANT }

/** A tool call as a chip: its result arrives on a later event (or a later history message), never with the call. */
data class UiTool(val id: String, val name: String, val input: String?, val result: String?, val done: Boolean)

/** One bubble. Reasoning, suggestions, todos and the reauth prompt are live-only: history never carries them. */
data class UiTurn(
    val role: Role,
    val text: String = "",
    val reasoning: String = "",
    val tools: List<UiTool> = emptyList(),
    val attachments: List<AttachmentRef> = emptyList(),
    val images: List<String> = emptyList(),
    val suggestions: List<String> = emptyList(),
    val todos: List<ResearchTodo> = emptyList(),
    val reauthServer: String? = null,
    val complete: Boolean = true,
    val stopped: Boolean = false,
)

/** Pure state transitions for the chat screen; no Android types, so it is unit-tested on the JVM. */
object ChatReducer {

    fun fromHistory(turns: List<ChatTurn>): List<UiTurn> = turns.map { turn ->
        UiTurn(
            role = if (turn.role == "user") Role.USER else Role.ASSISTANT,
            text = turn.text,
            attachments = turn.attachments.orEmpty(),
            tools = turn.toolCalls.orEmpty().map { UiTool(it.toolCallId, it.toolName, it.toolInput, it.toolResult, done = true) },
        )
    }

    /** Applies one stream event to the live assistant turn. */
    fun reduce(turn: UiTurn, event: StreamEvent): UiTurn = when (event.kind) {
        StreamEventType.TEXT_DELTA -> turn.copy(text = turn.text + event.text.orEmpty())
        StreamEventType.REASONING -> turn.copy(reasoning = turn.reasoning + event.text.orEmpty())
        // A research loop's later iteration supersedes the text so far, but its tool calls stay (ADR 0082).
        StreamEventType.TEXT_RESET -> turn.copy(text = "")
        StreamEventType.TOOL_CALL_START -> startTool(turn, event)
        StreamEventType.TOOL_CALL_END -> endTool(turn, event)
        StreamEventType.IMAGE -> event.attachmentId?.let { turn.copy(images = turn.images + it) } ?: turn
        StreamEventType.SUGGESTIONS -> turn.copy(suggestions = event.suggestions.orEmpty())
        StreamEventType.TODOS -> turn.copy(todos = event.todos.orEmpty())
        StreamEventType.MCP_REAUTH_REQUIRED -> turn.copy(reauthServer = event.text ?: event.mcpServerId)
        StreamEventType.DONE -> turn.copy(complete = true, stopped = event.stopped == true)
        // Shared state, MCP elicitation, the Auto router's routed label (ADR 0137: the app has no Auto picker yet) and anything
        // newer than this build have no surface in the app.
        StreamEventType.STATE_SNAPSHOT, StreamEventType.MCP_ELICITATION, StreamEventType.ROUTED, StreamEventType.UNKNOWN -> turn
    }

    private fun startTool(turn: UiTurn, event: StreamEvent): UiTurn {
        val id = event.toolCallId ?: return turn
        if (turn.tools.any { it.id == id }) return turn
        return turn.copy(tools = turn.tools + UiTool(id, event.toolName.orEmpty(), event.toolInput, null, done = false))
    }

    private fun endTool(turn: UiTurn, event: StreamEvent): UiTurn {
        val id = event.toolCallId ?: return turn
        val known = turn.tools.any { it.id == id }
        val tools = if (known) {
            turn.tools.map { if (it.id == id) it.copy(result = event.toolResult, done = true) else it }
        } else {
            turn.tools + UiTool(id, event.toolName.orEmpty(), event.toolInput, event.toolResult, done = true)
        }
        return turn.copy(tools = tools)
    }
}
