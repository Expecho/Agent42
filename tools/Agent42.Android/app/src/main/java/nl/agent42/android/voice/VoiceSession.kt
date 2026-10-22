package nl.agent42.android.voice

import android.content.Context
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import nl.agent42.android.AppContainer
import nl.agent42.android.BuildConfig
import nl.agent42.android.api.ChatRequest
import nl.agent42.android.api.SignedOutException
import nl.agent42.android.api.StreamEventType

/**
 * One hands-free voice call on one conversation: the [VoiceLoop] wired to the recognizer, the chat stream, the
 * speaker and the call audio route. Lives in [AppContainer] and is kept alive by [VoiceCallService], so it carries on
 * with the screen off or another app in front. Main-thread only.
 */
class VoiceSession(
    context: Context,
    private val container: AppContainer,
    val conversationId: String,
    val title: String,
) : VoiceIo {
    var state by mutableStateOf(VoiceState.IDLE)
        private set
    var heard by mutableStateOf("")
        private set
    var reply by mutableStateOf("")
        private set
    var output by mutableStateOf(AudioOutput.SPEAKER)
        private set
    var muted by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    /** Called whenever [state] changes, for the call notification. */
    var onState: (VoiceState) -> Unit = {}

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val loop = VoiceLoop(this)
    private val input = PlatformSpeechInput(
        context,
        onHeard = { text, final ->
            loop.onHeard(text, final)
            heard = loop.heard
        },
        onUnavailable = { error = it },
    )
    private val speech = SpeechOutput(context, scope, { container.api }) { loop.onSentenceSpoken() }
    private val route = AudioRoute(context) { changed ->
        output = changed
        loop.outputChanged(changed)
    }
    private var silence: Job? = null
    private var turn: Job? = null

    /** Null when Telecom refused the call; then the plain [route] does the audio. */
    private var telecom: TelecomCall? = TelecomCall(
        context,
        object : TelecomCall.Listener {
            override fun onOutput(output: AudioOutput) = onMain { applyOutput(output) }
            override fun onSystemMute(muted: Boolean) = onMain { if (muted != this@VoiceSession.muted) toggleMute() }
            override fun onHold(held: Boolean) = onMain { loop.hold(held) }
            override fun onDisconnected() = onMain { container.endVoice() }
            override fun onFailed(error: Throwable) = onMain {
                telecom = null
                trace("telecom failed (${error.javaClass.simpleName}), plain audio route instead")
                applyOutput(route.begin())
            }
        },
    )

    companion object {
        /** `adb logcat -s A42VOICE` shows every decision of the call. */
        const val TAG = "A42VOICE"

        /**
         * Conversation text in the trace: verbatim in a debug build (that is how the echo guards were found), only its
         * length in a release build, so the sideloaded app never writes what was said or replied to the system log.
         */
        fun redact(content: String): String = if (BuildConfig.DEBUG) content else "<${content.length} chars>"
    }

    fun start() {
        trace("call starting (telecom)")
        telecom?.start(title, conversationId)
        loop.start()
    }

    fun hangUp() {
        if (state == VoiceState.ENDED) return
        loop.hangUp()
        input.release()
        speech.release()
        telecom?.hangUp() ?: route.end()
        scope.cancel()
    }

    fun toggleMute() {
        muted = !muted
        loop.setMuted(muted)
    }

    fun toggleSpeaker() {
        val call = telecom
        if (call != null) call.toggleSpeaker() else applyOutput(route.toggleSpeaker())
    }

    private fun applyOutput(value: AudioOutput) {
        output = value
        trace("output $value")
        loop.outputChanged(value)
    }

    private fun onMain(block: () -> Unit) {
        scope.launch(Dispatchers.Main) { block() }
    }

    override fun listen(on: Boolean) {
        input.listen(on)
    }

    override fun sendTurn(text: String) {
        reply = ""
        heard = text
        error = null
        // LAZY, so `turn` names this job before its body can reach the `finally` below.
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                // No client tools in voice (ADR 0114): the server answers them "unavailable" at once. No model either:
                // the conversation keeps its own (a new one gets the user's default).
                container.api.chat(conversationId, ChatRequest(text, clientTools = emptyList())).collect { event ->
                    when (event.kind) {
                        StreamEventType.TEXT_DELTA -> reply += event.text.orEmpty()
                        StreamEventType.TEXT_RESET -> reply = ""
                        else -> Unit
                    }
                    loop.onReply(event)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: SignedOutException) {
                error = "Signed out — open the app to sign in again."
            } catch (e: IOException) {
                error = "Connection lost."
            } finally {
                // Only the current turn ends the reply; a barged-in turn's cancellation must not touch the next one.
                if (turn === coroutineContext[Job]) loop.onReplyEnded()
            }
        }
        turn = job
        job.start()
    }

    override fun speak(sentence: String) {
        speech.speak(sentence)
    }

    override fun stopSpeaking() {
        speech.stop()
    }

    override fun stopTurn() {
        turn?.cancel()
        turn = null
        // The server keeps running a turn whose stream we dropped (ADR 0046); only an explicit stop ends it (ADR 0078).
        CoroutineScope(Dispatchers.IO).launch { runCatching { container.api.stop(conversationId) } }
    }

    override fun scheduleSilence(delayMs: Long) {
        silence?.cancel()
        silence = scope.launch {
            delay(delayMs)
            loop.onSilence()
        }
    }

    override fun cancelSilence() {
        silence?.cancel()
    }

    override fun trace(message: String, content: String?) {
        Log.i(TAG, if (content == null) message else "$message: ${redact(content)}")
    }

    override fun stateChanged(state: VoiceState) {
        this.state = state
        if (state == VoiceState.LISTENING) heard = loop.heard
        onState(state)
    }
}
