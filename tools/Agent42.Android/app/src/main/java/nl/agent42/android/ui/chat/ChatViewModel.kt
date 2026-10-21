package nl.agent42.android.ui.chat

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.api.ApiException
import nl.agent42.android.api.ChatRequest
import nl.agent42.android.api.ModelInfo
import nl.agent42.android.api.SignedOutException
import nl.agent42.android.chat.ChatReducer
import nl.agent42.android.chat.Role
import nl.agent42.android.chat.UiTurn

class ChatViewModel(private val container: AppContainer, private val chat: Screen.Chat) : ViewModel() {
    val turns = mutableStateListOf<UiTurn>()
    var streaming by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var models by mutableStateOf<List<ModelInfo>>(emptyList())
        private set
    var model by mutableStateOf<String?>(null)
        private set

    init {
        viewModelScope.launch {
            call {
                val catalog = container.api.models()
                models = catalog.models.filter { it.isChatModel }
                model = if (chat.isNew) catalog.default else container.api.conversationModel(chat.id)
            }
        }
        if (!chat.isNew) reload()
    }

    fun pickModel(id: String) {
        model = id
    }

    fun send(text: String) {
        if (streaming || text.isBlank()) return
        val historySize = turns.size
        turns += UiTurn(Role.USER, text.trim())
        turns += UiTurn(Role.ASSISTANT, complete = false)
        streaming = true
        viewModelScope.launch {
            try {
                // The model rides the turn (the server persists a switch); an agent-bound chat uses its agent's model.
                val request = ChatRequest(text.trim(), model = if (chat.agentBound) null else model, clientTools = emptyList())
                container.api.chat(chat.id, request).collect { event ->
                    turns[turns.lastIndex] = ChatReducer.reduce(turns.last(), event)
                }
                if (!turns.last().complete) recover(historySize)
            } catch (e: CancellationException) {
                throw e
            } catch (e: SignedOutException) {
                container.signedOut()
            } catch (e: IOException) {
                // The stream died, but the server keeps running the turn (ADR 0046): read the reply from history.
                recover(historySize)
            } finally {
                streaming = false
            }
        }
    }

    /** Stops the turn on the server (ADR 0078); the stream then ends with a stopped `done`. */
    fun stop() {
        viewModelScope.launch { call { container.api.stop(chat.id) } }
    }

    private fun reload() {
        viewModelScope.launch { call { replaceWith(ChatReducer.fromHistory(container.api.messages(chat.id))) } }
    }

    /** Polls history until the turn we sent shows up with its reply, like the web client's recovery (send.js). */
    private suspend fun recover(historySize: Int) {
        error = "Connection lost; fetching the reply…"
        repeat(RECOVERY_POLLS) {
            delay(RECOVERY_INTERVAL_MS)
            val history = runCatching { container.api.messages(chat.id) }.getOrNull() ?: return@repeat
            if (history.size >= historySize + 2 && history.last().role == "assistant") {
                replaceWith(ChatReducer.fromHistory(history))
                error = null
                return
            }
        }
        error = "The reply didn't arrive. Pull it in later by reopening the chat."
    }

    private fun replaceWith(newTurns: List<UiTurn>) {
        turns.clear()
        turns.addAll(newTurns)
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
