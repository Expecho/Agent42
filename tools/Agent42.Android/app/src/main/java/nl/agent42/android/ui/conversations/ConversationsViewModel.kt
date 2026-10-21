package nl.agent42.android.ui.conversations

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import java.io.IOException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import nl.agent42.android.AppContainer
import nl.agent42.android.api.ApiException
import nl.agent42.android.api.ConversationInfo
import nl.agent42.android.api.SearchHit
import nl.agent42.android.api.SignedOutException

class ConversationsViewModel(private val container: AppContainer) : ViewModel() {
    var conversations by mutableStateOf<List<ConversationInfo>>(emptyList())
        private set
    var loading by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var query by mutableStateOf("")
        private set
    var hits by mutableStateOf<List<SearchHit>>(emptyList())
        private set

    private var searchJob: Job? = null

    fun load() {
        viewModelScope.launch {
            loading = true
            call { conversations = container.api.conversations() }
            loading = false
        }
    }

    fun search(text: String) {
        query = text
        searchJob?.cancel()
        if (text.isBlank()) {
            hits = emptyList()
            return
        }
        searchJob = viewModelScope.launch {
            delay(SEARCH_DEBOUNCE_MS)
            call { hits = container.api.search(text) }
        }
    }

    fun titleOf(conversationId: String) = conversations.firstOrNull { it.id == conversationId }?.title ?: "Chat"

    private suspend fun call(block: suspend () -> Unit) {
        try {
            block()
            error = null
        } catch (e: SignedOutException) {
            container.signedOut()
        } catch (e: ApiException) {
            // 409 from search: the user has no search index configured.
            error = if (e.code == 409) "Search isn't set up for this account." else e.message
        } catch (e: IOException) {
            error = "Can't reach the server."
        }
    }

    private companion object {
        const val SEARCH_DEBOUNCE_MS = 300L
    }
}
