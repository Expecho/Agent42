package nl.agent42.android.api

import java.io.IOException
import okhttp3.Interceptor
import okhttp3.Request
import okhttp3.Response

/**
 * How a request proves who the user is to Easy Auth. The backend reads only the principal headers Easy Auth injects
 * (CurrentUser.cs), so every implementation is something Easy Auth accepts in front of the app; the app itself never
 * sees or checks a token. Production uses [SessionCookie] from the WebView sign-in (ADR 0114); a DevHost uses [None].
 */
fun interface Credentials {
    fun applyTo(request: Request.Builder)

    companion object {
        /** A local DevHost has no Easy Auth: every request runs as `local-dev`. */
        val None = Credentials { }
    }
}

/** The Easy Auth session cookie from an interactive sign-in. */
class SessionCookie(private val value: String) : Credentials {
    override fun applyTo(request: Request.Builder) {
        request.header("Cookie", "$COOKIE_NAME=$value")
    }

    companion object {
        const val COOKIE_NAME = "AppServiceAuthSession"
    }
}

/** Easy Auth turned the request away: the app must sign in again. */
class SignedOutException(message: String) : IOException(message)

/**
 * "Signed out", however Easy Auth says it. Measured on prod: an unauthenticated request from this (non-browser) client
 * gets a bare 401; a browser-shaped one gets `RedirectToLoginPage`'s 302 to Entra (gotchas.md). So a 401, a redirect, or
 * an HTML page where JSON was expected all mean "sign in again", and redirects are never followed, or OkHttp would hand
 * the login page's HTML to the JSON parser.
 */
class SignedOutInterceptor : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val response = chain.proceed(chain.request())
        val html = response.header("Content-Type")?.startsWith("text/html") == true
        if (response.isRedirect || response.code == 401 || html) {
            response.close()
            throw SignedOutException("Signed out (HTTP ${response.code})")
        }
        return response
    }
}
