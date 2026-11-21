package nl.agent42.android.api

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * One line of the `POST /api/chat/{id}` NDJSON stream. Mirrors the server's `AgentStreamEvent` record field for
 * field: only the fields relevant to [type] are set, the rest stay null (the server omits nulls). The golden file
 * `tools/Agent42.Android/contract/stream-events.ndjson` pins both sides: the server's AndroidStreamContractTests
 * and this app's StreamEventParserTest read the same file.
 */
@Serializable
data class StreamEvent(
    val type: String,
    val text: String? = null,
    val toolName: String? = null,
    val toolCallId: String? = null,
    val toolInput: String? = null,
    val toolResult: String? = null,
    val inputTokens: Long? = null,
    val outputTokens: Long? = null,
    val cachedTokens: Long? = null,
    val attachmentId: String? = null,
    val mediaType: String? = null,
    val suggestions: List<String>? = null,
    val stopped: Boolean? = null,
    val mcpServerId: String? = null,
    val todos: List<ResearchTodo>? = null,
    val clientTool: Boolean? = null,
    val state: String? = null,
    val stateVersion: Long? = null,
    val elicitation: String? = null,
    val cacheWriteTokens: Long? = null,
    val costUsd: Double? = null,
    val costStatus: String? = null,
    // Set on a routed event (ADR 0137): the qualified id of the model the Auto router chose.
    val model: String? = null,
) {
    /** The event's kind; a type this build doesn't know is [StreamEventType.UNKNOWN], never a parse failure. */
    val kind: StreamEventType get() = StreamEventType.fromWire(type)
}

@Serializable
data class ResearchTodo(val id: Int, val title: String, val done: Boolean)

/** The server's `AgentStreamEventType`, by its camelCase wire name. */
enum class StreamEventType(val wire: String) {
    TEXT_DELTA("textDelta"),
    TOOL_CALL_START("toolCallStart"),
    TOOL_CALL_END("toolCallEnd"),
    IMAGE("image"),
    DONE("done"),
    SUGGESTIONS("suggestions"),
    REASONING("reasoning"),
    MCP_REAUTH_REQUIRED("mcpReauthRequired"),
    TODOS("todos"),
    TEXT_RESET("textReset"),
    STATE_SNAPSHOT("stateSnapshot"),
    MCP_ELICITATION("mcpElicitation"),
    ROUTED("routed"),
    UNKNOWN("");

    companion object {
        private val byWire = entries.filter { it != UNKNOWN }.associateBy { it.wire }

        fun fromWire(wire: String): StreamEventType = byWire[wire] ?: UNKNOWN
    }
}

object StreamEventParser {
    // A newer server may add fields; an older app must keep reading the stream rather than fail the turn.
    private val json = Json { ignoreUnknownKeys = true }

    /** Parses one NDJSON line, or returns null for a blank line. */
    fun parse(line: String): StreamEvent? =
        if (line.isBlank()) null else json.decodeFromString(StreamEvent.serializer(), line)
}
