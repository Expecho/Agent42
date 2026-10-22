package nl.agent42.android.session

import nl.agent42.android.api.Credentials
import nl.agent42.android.api.SessionCookie

/** Where the app talks to, and the Easy Auth session cookie it signed in with (ADR 0114). */
data class Session(val baseUrl: String, val cookie: String? = null) {

    /**
     * An https server sits behind Easy Auth and needs the WebView sign-in. A plain-http one is a local DevHost (reached
     * through `adb reverse`), which has no Easy Auth: every request runs as `local-dev`.
     */
    val needsSignIn: Boolean get() = isEasyAuth(baseUrl) && cookie.isNullOrBlank()

    val credentials: Credentials
        get() = if (isEasyAuth(baseUrl) && !cookie.isNullOrBlank()) SessionCookie(cookie) else Credentials.None

    companion object {
        const val DEFAULT_URL = "https://chat.agent42.nl"

        fun isEasyAuth(baseUrl: String) = baseUrl.trim().startsWith("https://", ignoreCase = true)

        /** Trims and drops a trailing slash; adds https:// when no scheme was typed. Null when it isn't a URL. */
        fun normalizeUrl(input: String): String? {
            val trimmed = input.trim()
            if (trimmed.isEmpty() || trimmed.any { it.isWhitespace() }) return null
            val scheme = if (trimmed.contains("://")) trimmed.substringBefore("://").lowercase() else "https"
            if (scheme != "https" && scheme != "http") return null
            val host = trimmed.substringAfter("://").trimEnd('/')
            return if (host.isEmpty()) null else "$scheme://$host"
        }
    }
}

interface SessionStore {
    fun load(): Session?
    fun save(session: Session)
}
