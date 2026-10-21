package nl.agent42.android

import android.content.Context
import android.webkit.CookieManager
import androidx.compose.runtime.mutableStateListOf
import nl.agent42.android.api.Agent42Api
import nl.agent42.android.session.KeystoreSessionStore
import nl.agent42.android.session.Session
import okhttp3.OkHttpClient

/** The app's screens. A back stack of these is the whole navigation model. */
sealed interface Screen {
    data object Server : Screen
    data object SignIn : Screen
    data object Conversations : Screen
    data class Chat(val id: String, val title: String, val isNew: Boolean, val agentBound: Boolean) : Screen
}

/**
 * Process-wide state: the session, the API client built from it, and the back stack. Lives in [Agent42App], so it
 * survives activity recreation (rotation).
 */
class AppContainer(context: Context) {
    private val store = KeystoreSessionStore(context)
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
    }

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
