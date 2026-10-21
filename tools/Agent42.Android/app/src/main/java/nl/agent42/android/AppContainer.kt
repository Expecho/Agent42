package nl.agent42.android

import android.content.ContentResolver
import android.content.Context
import android.webkit.CookieManager
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import java.io.File
import java.util.UUID
import nl.agent42.android.api.Agent42Api
import nl.agent42.android.chat.SharedContent
import nl.agent42.android.location.LocationSource
import nl.agent42.android.voice.VoiceCallService
import nl.agent42.android.voice.VoiceSession
import nl.agent42.android.session.KeystoreSessionStore
import nl.agent42.android.session.Session
import okhttp3.OkHttpClient

/** The app's screens. A back stack of these is the whole navigation model. */
sealed interface Screen {
    data object Server : Screen
    data object SignIn : Screen
    data object Conversations : Screen
    data class Chat(val id: String, val title: String, val isNew: Boolean, val agentBound: Boolean) : Screen
    data class Voice(val chat: Chat) : Screen
}

/**
 * Process-wide state: the session, the API client built from it, and the back stack. Lives in [Agent42App], so it
 * survives activity recreation (rotation).
 */
class AppContainer(private val context: Context) {
    private val store = KeystoreSessionStore(context)

    /** The voice call in progress, if any (one at a time); kept alive by VoiceCallService. */
    var voice by mutableStateOf<VoiceSession?>(null)
        private set

    /** Bumped when a call ends, so the chat it ran on re-reads its history. */
    var voiceEnded by mutableIntStateOf(0)
        private set

    fun startVoice(chat: Screen.Chat): VoiceSession {
        voice?.takeIf { it.conversationId == chat.id }?.let { return it }
        endVoice()
        return VoiceSession(context, this, chat.id, chat.title).also {
            voice = it
            it.start()
            VoiceCallService.start(context)
        }
    }

    fun endVoice() {
        val session = voice ?: return
        voice = null
        session.hangUp()
        voiceEnded++
        if (stack.lastOrNull() is Screen.Voice) back()
    }

    val resolver: ContentResolver = context.contentResolver
    val location = LocationSource(context)
    val cacheDir: File = context.cacheDir
    private val http = OkHttpClient()
    private var cachedApi: Pair<Session, Agent42Api>? = null

    var session: Session? = store.load()
        private set

    val stack = mutableStateListOf(startScreen(session))

    val api: Agent42Api
        get() {
            val current = checkNotNull(session) { "No server configured" }
            cachedApi?.takeIf { it.first == current }?.let { return it.second }
            return Agent42Api(current.baseUrl, current.credentials, http).also { cachedApi = current to it }
        }

    fun setServer(baseUrl: String) {
        // A different server never inherits the old one's session cookie.
        val keep = session?.takeIf { it.baseUrl == baseUrl }?.cookie
        update(Session(baseUrl, keep))
        resetTo(startScreen(session))
    }

    fun signedIn(cookie: String) {
        update(checkNotNull(session).copy(cookie = cookie))
        resetTo(Screen.Conversations)
        if (pendingShare != null) navigate(newChat())
    }

    /** A share waiting for a new chat to pick it up; it survives a detour through sign-in. */
    var pendingShare: SharedContent? = null
        private set

    fun share(content: SharedContent) {
        pendingShare = content
        if (stack.last() != Screen.Server && stack.last() != Screen.SignIn) navigate(newChat())
    }

    fun takeShare(): SharedContent? = pendingShare.also { pendingShare = null }

    fun newChat() = Screen.Chat(UUID.randomUUID().toString(), "New chat", isNew = true, agentBound = false)

    /** Easy Auth turned a request away (a 401, a redirect or an HTML page): drop the cookie and sign in again. */
    fun signedOut() {
        session?.let { update(it.copy(cookie = null)) }
        CookieManager.getInstance().removeAllCookies(null)
        resetTo(startScreen(session))
    }

    fun navigate(screen: Screen) {
        stack.add(screen)
    }

    /** Pops one screen; false when there is nothing to go back to (the system then closes the app). */
    fun back(): Boolean {
        if (stack.size <= 1) return false
        stack.removeAt(stack.lastIndex)
        return true
    }

    private fun update(newSession: Session) {
        session = newSession
        store.save(newSession)
    }

    private fun resetTo(screen: Screen) {
        stack.clear()
        stack.add(screen)
    }

    private companion object {
        fun startScreen(session: Session?): Screen = when {
            session == null -> Screen.Server
            session.needsSignIn -> Screen.SignIn
            else -> Screen.Conversations
        }
    }
}
