package nl.agent42.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import nl.agent42.android.chat.SharedContent
import nl.agent42.android.theme.Agent42Theme
import nl.agent42.android.ui.AppRoot

class MainActivity : ComponentActivity() {
    private val container get() = (application as Agent42App).container

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // A recreated activity (rotation) must not replay the share or entry point it was started with.
        if (savedInstanceState == null) handle(intent)
        enableEdgeToEdge()
        setContent {
            Agent42Theme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    AppRoot(container)
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    private fun handle(intent: Intent?) {
        when (intent?.action) {
            // The tile, widget and "Voice call" shortcut (ACTION_VOICE_CALL), and the system assist gesture when Agent 42
            // is the digital assistant (ACTION_ASSIST). Android 14 only lets a foreground app start a microphone call,
            // which is why every entry point opens this activity rather than the service.
            ACTION_VOICE_CALL, Intent.ACTION_ASSIST, Intent.ACTION_VOICE_COMMAND -> container.voiceEntry()
            ACTION_NEW_CHAT -> if (container.stack.last() is Screen.Conversations) container.navigate(container.newChat())
            else -> handleShare(intent)
        }
    }

    /** The share sheet (ACTION_SEND / SEND_MULTIPLE), like the PWA's share target: a new chat, prefilled. */
    private fun handleShare(intent: Intent?) {
        if (intent?.action != Intent.ACTION_SEND && intent?.action != Intent.ACTION_SEND_MULTIPLE) return
        val files = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java))
            else -> intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
        }
        val text = SharedContent.text(intent.getStringExtra(Intent.EXTRA_SUBJECT), intent.getStringExtra(Intent.EXTRA_TEXT))
        if (text == null && files.isEmpty()) return
        container.share(SharedContent(text, files.map(Uri::toString)))
    }

    companion object {
        const val ACTION_VOICE_CALL = "nl.agent42.android.action.VOICE_CALL"
        const val ACTION_NEW_CHAT = "nl.agent42.android.action.NEW_CHAT"

        fun voiceCallIntent(context: android.content.Context): Intent =
            Intent(context, MainActivity::class.java).setAction(ACTION_VOICE_CALL)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
    }
}
