package nl.agent42.android.ui.chat

import android.net.Uri
import android.provider.OpenableColumns
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.api.Agent42Api
import nl.agent42.android.api.ApiException
import nl.agent42.android.api.ChatRequest
import nl.agent42.android.api.ModelInfo
import nl.agent42.android.api.SignedOutException
import nl.agent42.android.api.StreamEvent
import nl.agent42.android.api.StreamEventType
import nl.agent42.android.api.UploadResult
import nl.agent42.android.chat.ChatReducer
import nl.agent42.android.chat.ClientTools
import nl.agent42.android.chat.Role
import nl.agent42.android.chat.UiTurn

/** A file on its way to the next turn: uploading, ready (an image id for `attachmentIds`) or failed. */
data class PendingFile(val key: Long, val name: String, val id: String? = null, val error: String? = null) {
    val uploading get() = id == null && error == null
}

/** The confirm dialog on screen: the model's action text, answered by Approve (true) or anything else (false). */
class ConfirmRequest(val text: String, val answer: CompletableDeferred<Boolean>)

class ChatViewModel(private val container: AppContainer, private val chat: Screen.Chat) : ViewModel() {
    val turns = mutableStateListOf<UiTurn>()
    val files = mutableStateListOf<PendingFile>()
    var draft by mutableStateOf("")
    var streaming by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var notice by mutableStateOf<String?>(null)
        private set
    var models by mutableStateOf<List<ModelInfo>>(emptyList())
        private set
    var model by mutableStateOf<String?>(null)
        private set

    /** Set by the screen from its lifecycle: only an on-screen chat advertises client tools (ADR 0114). */
    var foreground = false

    var confirm by mutableStateOf<ConfirmRequest?>(null)
        private set

    /** Non-null while the screen should ask for the location permission; completed with the grant. */
    var locationPermission by mutableStateOf<CompletableDeferred<Boolean>?>(null)
        private set

    private val api: Agent42Api get() = container.api
    private val handledCalls = mutableSetOf<String>()
    private val dialogs = Mutex()
    private val images = mutableMapOf<String, ByteArray>()
    private var nextKey = 0L

    init {
        viewModelScope.launch {
            call {
                val catalog = api.models()
                models = catalog.models.filter { it.isChatModel }
                model = if (chat.isNew) catalog.default else api.conversationModel(chat.id)
            }
        }
        if (chat.isNew) {
            container.takeShare()?.let { share ->
                share.text?.let { draft = it }
                share.files.forEach { attach(Uri.parse(it)) }
            }
        } else {
            reload()
        }
    }

    fun pickModel(id: String) {
        model = id
    }

    fun send(text: String = draft) {
        if (streaming || text.isBlank() || files.any { it.uploading }) return
        val historySize = turns.size
        val ids = files.mapNotNull { it.id }
        val sent = turns.size
        turns += UiTurn(Role.USER, text.trim(), attachments = emptyList())
        turns += UiTurn(Role.ASSISTANT, complete = false)
        draft = ""
        files.clear()
        notice = null
        streaming = true
        viewModelScope.launch {
            try {
                val request = ChatRequest(
                    text.trim(),
                    // The model rides the turn (the server persists a switch); an agent chat uses its agent's model.
                    model = if (chat.agentBound) null else model,
                    attachmentIds = ids.ifEmpty { null },
                    clientTools = ClientTools.advertised(chatInForeground = foreground, voice = false),
                )
                api.chat(chat.id, request).collect(::onEvent)
                if (!turns.last().complete) recover(historySize)
                else if (ids.isNotEmpty()) reloadAfter(sent)
            } catch (e: CancellationException) {
                throw e
            } catch (e: SignedOutException) {
                container.signedOut()
            } catch (e: IOException) {
                // The stream died, but the server keeps running the turn (ADR 0046): read the reply from history.
                recover(historySize)
            } finally {
                streaming = false
                // The turn is over: a confirm still on screen belongs to a call the server no longer waits for.
                confirm?.answer?.complete(false)
            }
        }
    }

    /** Stops the turn on the server (ADR 0078); the stream then ends with a stopped `done`. */
    fun stop() {
        viewModelScope.launch { call { api.stop(chat.id) } }
    }

    /** Uploads a picked, captured or shared file at once, so the turn only carries ids (like the web composer). */
    fun attach(uri: Uri) {
        val key = nextKey++
        val name = displayName(uri) ?: "file"
        files += PendingFile(key, name)
        viewModelScope.launch {
            val outcome = runCatching {
                val (bytes, mediaType) = withContext(Dispatchers.IO) { read(uri) }
                api.upload(chat.id, bytes, name, mediaType)
            }
            val index = files.indexOfFirst { it.key == key }
            if (index < 0) return@launch
            outcome.onSuccess { result ->
                when (result) {
                    is UploadResult.Image -> files[index] = files[index].copy(id = result.id)
                    is UploadResult.Document -> {
                        files.removeAt(index)
                        notice = "${result.fileName} was added to your documents."
                    }
                }
            }.onFailure { e ->
                if (e is SignedOutException) container.signedOut()
                files[index] = files[index].copy(error = uploadError(e))
            }
        }
    }

    fun removeFile(key: Long) {
        files.removeAll { it.key == key }
    }

    /** An attachment's image bytes, fetched once through the authenticated proxy. */
    suspend fun image(attachmentId: String): ByteArray? = images[attachmentId]
        ?: runCatching { api.attachment(chat.id, attachmentId) }.getOrNull()?.also { images[attachmentId] = it }

    fun answerConfirm(approved: Boolean) {
        confirm?.answer?.complete(approved)
    }

    fun answerLocationPermission(granted: Boolean) {
        locationPermission?.complete(granted)
    }

    private fun onEvent(event: StreamEvent) {
        turns[turns.lastIndex] = ChatReducer.reduce(turns.last(), event)
        if (event.kind == StreamEventType.TOOL_CALL_START && event.clientTool == true) {
            runClientTool(event.toolCallId, event.toolName, event.toolInput)
        }
    }

    /**
     * Runs a call the server is waiting on and posts its result. Never awaited by the stream reader, so the turn's own
     * events (including a stop) keep arriving while a dialog is open. A late answer is a 404 and is ignored.
     */
    private fun runClientTool(toolCallId: String?, toolName: String?, toolInput: String?) {
        if (toolCallId == null || !handledCalls.add(toolCallId)) return
        viewModelScope.launch {
            try {
                val result = when (toolName) {
                    ClientTools.LOCATION -> location()
                    ClientTools.CONFIRM -> confirm(toolInput)
                    else -> ClientTools.UNAVAILABLE
                }
                runCatching { api.toolResult(chat.id, toolCallId, result) }
            } finally {
                handledCalls.remove(toolCallId)
            }
        }
    }

    private suspend fun location(): JsonObject {
        if (!container.location.granted()) {
            if (!foreground) return ClientTools.NOT_SHARED
            val asked = CompletableDeferred<Boolean>()
            locationPermission = asked
            val granted = try { asked.await() } finally { locationPermission = null }
            if (!granted) return ClientTools.NOT_SHARED
        }
        return container.location.fix()?.let(ClientTools::location) ?: ClientTools.NO_FIX
    }

    // One dialog at a time; later requests wait their turn, as on the web.
    private suspend fun confirm(toolInput: String?): JsonObject = dialogs.withLock {
        val request = ConfirmRequest(ClientTools.confirmPrompt(toolInput), CompletableDeferred())
        confirm = request
        try {
            ClientTools.confirmed(request.answer.await())
        } finally {
            confirm = null
        }
    }

    fun refresh() {
        if (!streaming) reload()
    }

    private fun reload() {
        viewModelScope.launch {
            call {
                replaceWith(ChatReducer.fromHistory(api.messages(chat.id)))
                // Calls still waiting for a client whose stream died (ADR 0046): answer them now.
                api.pendingToolCalls(chat.id).forEach { runClientTool(it.toolCallId, it.toolName, it.toolInput) }
            }
        }
    }

    // A sent image only gets its stored name and media type from history; refresh once the turn is done.
    private suspend fun reloadAfter(sent: Int) {
        val history = runCatching { api.messages(chat.id) }.getOrNull() ?: return
        if (history.size >= sent + 2) replaceWith(ChatReducer.fromHistory(history).withLiveExtras(turns.lastOrNull()))
    }

    /** Polls history until the turn we sent shows up with its reply, like the web client's recovery (send.js). */
    private suspend fun recover(historySize: Int) {
        error = "Connection lost; fetching the reply…"
        repeat(RECOVERY_POLLS) {
            delay(RECOVERY_INTERVAL_MS)
            val history = runCatching { api.messages(chat.id) }.getOrNull() ?: return@repeat
            if (history.size >= historySize + 2 && history.last().role == "assistant") {
                replaceWith(ChatReducer.fromHistory(history))
                error = null
                return
            }
        }
        error = "The reply didn't arrive. Reopen the chat to fetch it later."
    }

    private fun replaceWith(newTurns: List<UiTurn>) {
        turns.clear()
        turns.addAll(newTurns)
    }

    private fun read(uri: Uri): Pair<ByteArray, String> {
        val size = container.resolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use {
            if (it.moveToFirst() && !it.isNull(0)) it.getLong(0) else null
        }
        require(size == null || size <= Agent42Api.MAX_ATTACHMENT_BYTES) { "Attachments are limited to 10 MB." }
        val bytes = container.resolver.openInputStream(uri)?.use { it.readBytes() } ?: throw IOException("Can't read the file.")
        return bytes to (container.resolver.getType(uri) ?: "application/octet-stream")
    }

    private fun displayName(uri: Uri): String? =
        container.resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        } ?: uri.lastPathSegment

    private fun uploadError(e: Throwable): String = when (e) {
        is IllegalArgumentException -> e.message ?: "Too large."
        is ApiException -> if (e.code == 400) "Not a supported file type." else "Upload failed."
        else -> "Upload failed."
    }

    private suspend fun call(block: suspend () -> Unit) {
        try {
            block()
        } catch (e: SignedOutException) {
            container.signedOut()
        } catch (e: ApiException) {
            error = e.message
        } catch (e: IOException) {
            error = "Can't reach the server."
        }
    }

    private companion object {
        const val RECOVERY_POLLS = 15
        const val RECOVERY_INTERVAL_MS = 2_000L
    }
}

/** History has no suggestions; keep the live turn's on the refreshed last turn so the chips don't vanish. */
private fun List<UiTurn>.withLiveExtras(live: UiTurn?): List<UiTurn> {
    if (live == null || isEmpty()) return this
    return dropLast(1) + last().copy(suggestions = live.suggestions, stopped = live.stopped, images = live.images)
}
