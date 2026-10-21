package nl.agent42.android.api

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

// The JSON shapes of the Agent42 endpoints this app calls. Each mirrors a server record (named in its comment); the
// server writes camelCase and may add fields, which the parser ignores.

/** `ConversationInfo` (GET /api/conversations): pinned first, then most recently used. */
@Serializable
data class ConversationInfo(
    val id: String,
    val title: String,
    val updatedAt: String,
    val agentId: String? = null,
    val agentName: String? = null,
    val pinned: Boolean = false,
    val groupId: String? = null,
)

/** `ChatTurn` (GET /api/conversations/{id}/messages). Reasoning is live-only and never in history (ADR 0071). */
@Serializable
data class ChatTurn(
    val role: String,
    val text: String,
    val attachments: List<AttachmentRef>? = null,
    val toolCalls: List<ChatToolCall>? = null,
    val artifacts: List<ArtifactInfo>? = null,
)

@Serializable
data class AttachmentRef(val id: String, val mediaType: String, val fileName: String)

@Serializable
data class ChatToolCall(
    val toolCallId: String,
    val toolName: String,
    val toolInput: String? = null,
    val toolResult: String? = null,
)

@Serializable
data class ArtifactInfo(val artifactId: String, val version: Int, val kind: String, val title: String)

/** `SearchResponse` (GET /api/search?q=). */
@Serializable
data class SearchResponse(val hits: List<SearchHit>)

@Serializable
data class SearchHit(val conversationId: String, val role: String, val text: String, val ordinal: Int)

/** `ModelCatalogResult` (GET /api/models). */
@Serializable
data class ModelCatalog(val default: String, val models: List<ModelInfo>)

@Serializable
data class ModelInfo(
    val id: String,
    val provider: String,
    val displayName: String? = null,
    val supportsTools: Boolean = false,
    val supportsVision: Boolean = false,
    val supportsImageGeneration: Boolean = false,
    val supportsEmbeddings: Boolean = false,
    val supportsSpeech: Boolean = false,
    // ADR 0113: a speech-to-text model, picked separately, never a chat model.
    val supportsTranscription: Boolean = false,
    val providerDisplayName: String? = null,
    // ADR 0108: usable only by Copilot agents, so never offered in a chat's model picker.
    val copilotOnly: Boolean = false,
) {
    /** Whether a chat's model picker offers it: a chat model, not an embedding/speech/transcription/image/Copilot-only one. */
    val isChatModel: Boolean
        get() = !copilotOnly && !supportsEmbeddings && !supportsSpeech && !supportsTranscription && !supportsImageGeneration
}

/** `PendingClientToolCall` (GET /api/chat/{id}/pending-tool-calls): calls waiting on this client after a dropped stream. */
@Serializable
data class PendingClientToolCall(val toolCallId: String, val toolName: String, val toolInput: String? = null)

/** `ChatRequest` (POST /api/chat/{id}). Only the fields this app sends; nulls are omitted. */
@Serializable
data class ChatRequest(
    val message: String,
    val model: String? = null,
    val attachmentIds: List<String>? = null,
    // The client tools this client can answer right now (ADR 0089). The server answers every other one
    // {"status":"unavailable"} at once, so a tool missing here can never stall the turn.
    val clientTools: List<String>? = null,
)

@Serializable
internal data class ToolResultRequest(val toolCallId: String, val result: JsonElement)

@Serializable
internal data class SpeechRequest(val text: String)

@Serializable
internal data class UploadResponse(
    val id: String? = null,
    val kind: String? = null,
    val mediaType: String? = null,
    val fileName: String? = null,
    val chunkCount: Int? = null,
)

/** What an upload became: an image to send with the next turn, or a document added to the user's documents (RAG). */
sealed interface UploadResult {
    data class Image(val id: String, val mediaType: String, val fileName: String) : UploadResult

    data class Document(val fileName: String, val chunkCount: Int) : UploadResult
}
