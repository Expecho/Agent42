package nl.agent42.android.ui.voice

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.theme.Agent42
import nl.agent42.android.theme.Prose
import nl.agent42.android.ui.Agent42Background
import nl.agent42.android.ui.BrandMark
import nl.agent42.android.ui.LineButton
import nl.agent42.android.ui.MetaLabel
import nl.agent42.android.voice.AudioOutput
import nl.agent42.android.voice.VoiceState

/** The call screen. Back hangs up; switching apps or turning the screen off keeps the call going. */
@Composable
fun VoiceScreen(container: AppContainer, screen: Screen.Voice) {
    val context = LocalContext.current
    val permissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { grants ->
        if (grants[Manifest.permission.RECORD_AUDIO] == true) container.startVoice(screen.chat) else container.back()
    }
    LaunchedEffect(screen) {
        val mic = context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        if (mic) container.startVoice(screen.chat)
        else permissions.launch(arrayOf(Manifest.permission.RECORD_AUDIO, Manifest.permission.POST_NOTIFICATIONS))
    }
    BackHandler { container.endVoice() }

    val c = Agent42.colors
    val session = container.voice?.takeIf { it.conversationId == screen.chat.id }
    val state = session?.state ?: VoiceState.IDLE
    Agent42Background {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            MetaLabel(screen.chat.title.uppercase())
            Box(Modifier.padding(top = 24.dp, bottom = 8.dp), contentAlignment = Alignment.Center) {
                Halo(state, muted = session?.muted == true)
                BrandMark(size = 96.dp)
            }
            Text(
                when (state) {
                    VoiceState.LISTENING -> if (session?.muted == true) "muted" else "listening"
                    VoiceState.THINKING -> "thinking"
                    VoiceState.SPEAKING -> "speaking — talk to interrupt"
                    VoiceState.ENDED -> "call ended"
                    VoiceState.IDLE -> "connecting"
                },
                style = MaterialTheme.typography.titleMedium,
                color = if (state == VoiceState.SPEAKING || state == VoiceState.LISTENING) c.amber else c.inkDim,
            )
            session?.heard?.takeIf { it.isNotBlank() }?.let {
                Text("“$it”", style = Prose.copy(fontStyle = FontStyle.Italic), color = c.inkDim, textAlign = TextAlign.Center)
            }
            Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState())) {
                session?.reply?.takeIf { it.isNotBlank() }?.let {
                    MetaLabel("AGENT 42", Modifier.padding(bottom = 6.dp))
                    Text(it, style = Prose, color = c.ink)
                }
            }
            session?.error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = c.red) }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.fillMaxWidth()) {
                LineButton(if (session?.muted == true) "unmute" else "mute", { session?.toggleMute() }, enabled = session != null, modifier = Modifier.weight(1f))
                LineButton(
                    when (session?.output) {
                        AudioOutput.EARPIECE -> "earpiece"
                        AudioOutput.BLUETOOTH -> "bluetooth"
                        AudioOutput.WIRED_HEADSET -> "headset"
                        else -> "speaker"
                    },
                    { session?.toggleSpeaker() },
                    enabled = session != null && session.output in setOf(AudioOutput.SPEAKER, AudioOutput.EARPIECE),
                    modifier = Modifier.weight(1f),
                )
            }
            Box(
                Modifier
                    .fillMaxWidth()
                    .background(c.red, RoundedCornerShape(12.dp))
                    .clickable { container.endVoice() }
                    .padding(vertical = 16.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text("hang up", style = MaterialTheme.typography.labelLarge, color = c.bg)
            }
        }
    }
}

/** The glow behind the mark: an amber pulse while listening, a slow dim breath while thinking, a full glow while speaking. */
@Composable
private fun Halo(state: VoiceState, muted: Boolean) {
    val c = Agent42.colors
    val transition = rememberInfiniteTransition(label = "halo")
    val pulse by transition.animateFloat(
        0.55f, 1f,
        infiniteRepeatable(tween(if (state == VoiceState.THINKING) 1600 else 900), RepeatMode.Reverse),
        label = "halo-pulse",
    )
    val (color, strength) = when {
        muted || state == VoiceState.ENDED || state == VoiceState.IDLE -> c.inkFaint to 0.25f
        state == VoiceState.SPEAKING -> c.amber to 1f
        state == VoiceState.LISTENING -> c.amber to pulse
        else -> c.inkDim to pulse * 0.6f
    }
    Box(
        Modifier
            .size(220.dp)
            .drawBehind {
                drawCircle(Brush.radialGradient(listOf(color.copy(alpha = 0.32f * strength), Color.Transparent)))
            },
    )
}
