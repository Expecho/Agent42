package nl.agent42.android.ui.voice

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
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

    val session = container.voice?.takeIf { it.conversationId == screen.chat.id }
    Column(
        Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text(screen.chat.title, style = MaterialTheme.typography.titleMedium)
        Text(
            when (session?.state) {
                VoiceState.LISTENING -> if (session.muted) "Muted" else "Listening…"
                VoiceState.THINKING -> "Thinking…"
                VoiceState.SPEAKING -> "Speaking — talk to interrupt"
                VoiceState.ENDED -> "Call ended"
                else -> "Starting…"
            },
            style = MaterialTheme.typography.headlineMedium,
        )
        session?.heard?.takeIf { it.isNotBlank() }?.let {
            Text("“$it”", style = MaterialTheme.typography.bodyLarge, textAlign = TextAlign.Center)
        }
        Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState())) {
            session?.reply?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
        }
        session?.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedButton(onClick = { session?.toggleMute() }, enabled = session != null) {
                Text(if (session?.muted == true) "Unmute" else "Mute")
            }
            OutlinedButton(
                onClick = { session?.toggleSpeaker() },
                enabled = session != null && session.output in setOf(AudioOutput.SPEAKER, AudioOutput.EARPIECE),
            ) {
                Text(
                    when (session?.output) {
                        AudioOutput.EARPIECE -> "Earpiece"
                        AudioOutput.BLUETOOTH -> "Bluetooth"
                        AudioOutput.WIRED_HEADSET -> "Headset"
                        else -> "Speaker"
                    },
                )
            }
        }
        Button(
            onClick = { container.endVoice() },
            colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
            modifier = Modifier.fillMaxWidth(),
        ) { Text("Hang up") }
        Spacer(Modifier.padding(4.dp))
    }
}
