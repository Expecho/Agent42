package nl.agent42.android.ui.signin

import android.annotation.SuppressLint
import android.webkit.CookieManager
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import nl.agent42.android.AppContainer
import nl.agent42.android.api.SessionCookie

/**
 * Easy Auth sign-in in a WebView (ADR 0114): the Entra pages run inside it, and once it is back on the Agent42 host the
 * `AppServiceAuthSession` cookie is read from the WebView's cookie jar and handed to the API client. The WebView never
 * shows the web app itself.
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun SignInScreen(container: AppContainer) {
    val base = checkNotNull(container.session).baseUrl
    AndroidView(
        modifier = Modifier.fillMaxSize().safeDrawingPadding(),
        factory = { context ->
            CookieManager.getInstance().removeAllCookies(null)
            WebView(context).apply {
                settings.javaScriptEnabled = true
                settings.domStorageEnabled = true
                webViewClient = object : WebViewClient() {
                    private var done = false

                    override fun onPageFinished(view: WebView, url: String) {
                        if (done || !url.startsWith(base)) return
                        sessionCookie(CookieManager.getInstance().getCookie(base))?.let {
                            done = true
                            view.stopLoading()
                            container.signedIn(it)
                        }
                    }
                }
                loadUrl("$base/.auth/login/aad?post_login_redirect_uri=/")
            }
        },
    )
}

/** The session cookie's value out of a `Cookie` header string, or null when it isn't there yet. */
internal fun sessionCookie(cookies: String?): String? = cookies.orEmpty()
    .split(';')
    .map { it.trim() }
    .firstOrNull { it.startsWith("${SessionCookie.COOKIE_NAME}=") }
    ?.substringAfter('=')
    ?.takeIf { it.isNotBlank() }
