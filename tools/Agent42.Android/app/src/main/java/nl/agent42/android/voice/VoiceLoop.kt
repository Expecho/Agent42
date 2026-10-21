package nl.agent42.android.voice

import nl.agent42.android.api.StreamEvent
import nl.agent42.android.api.StreamEventType

enum class VoiceState { IDLE, LISTENING, THINKING, SPEAKING, ENDED }

/** Where the reply plays. The app's duplex rule depends on it. */
enum class AudioOutput { SPEAKER, EARPIECE, WIRED_HEADSET, BLUETOOTH }

/**
 * Whether the mic stays live while the assistant talks. ADR 0114: on the call audio path the echo canceller removes the
 * reply, so the app is full duplex on the speaker too; ADR 0079's half duplex (mic closed on the speaker) stays
 * available as the fallback for a phone where that proves noisy.
 */
data class DuplexPolicy(val fullDuplexOnSpeaker: Boolean = true) {
    fun listenWhileSpeaking(output: AudioOutput): Boolean = output != AudioOutput.SPEAKER || fullDuplexOnSpeaker
}

/** What the loop asks of the outside world: the recognizer, the chat stream, the speaker, a timer. */
interface VoiceIo {
    fun listen(on: Boolean)
    fun sendTurn(text: String)
    fun speak(sentence: String)
    fun stopSpeaking()
    fun stopTurn()
    fun scheduleSilence(delayMs: Long)
    fun cancelSilence()
    fun stateChanged(state: VoiceState)
}

/**
 * The hands-free conversation as a pure state machine (ADR 0037's loop, ported; ADR 0114's duplex rule). No Android
 * types: every effect goes through [VoiceIo], so the whole flow is unit-tested on the JVM.
 *
 * listening --(silence after speech)--> thinking --(first sentence)--> speaking --(reply spoken)--> listening
 * Talking over thinking/speaking (when the mic is live) is a barge-in: stop the audio, stop the turn on the server
 * (ADR 0078), and the interrupting words start the next utterance.
 */
class VoiceLoop(private val io: VoiceIo, var policy: DuplexPolicy = DuplexPolicy()) {
    var state = VoiceState.IDLE
        private set
    var output = AudioOutput.SPEAKER
    var muted = false
        private set

    /** What the user said so far in this utterance. */
    var heard = ""
        private set

    /** The reply being spoken, for the screen and the echo check. */
    var spoken = ""
        private set

    private var finals = ""
    private var lastSubmitted = ""
    private val sentences = SentenceBuffer()
    private var replyEnded = false
    private var queued = 0

    fun start() {
        if (state != VoiceState.IDLE) return
        enter(VoiceState.LISTENING)
    }

    fun hangUp() {
        if (state == VoiceState.ENDED) return
        if (state == VoiceState.THINKING || state == VoiceState.SPEAKING) {
            io.stopSpeaking()
            io.stopTurn()
        }
        io.cancelSilence()
        enter(VoiceState.ENDED)
    }

    fun setMuted(value: Boolean) {
        muted = value
        updateMic()
    }

    fun outputChanged(value: AudioOutput) {
        output = value
        updateMic()
    }

    /** A recognition result (partial or final) for the current utterance. */
    fun onHeard(text: String, final: Boolean) {
        val t = text.trim()
        if (t.isEmpty() || muted) return
        when (state) {
            VoiceState.LISTENING -> {
                if (final) finals = "$finals $t".trim()
                heard = if (final) finals else "$finals $t".trim()
                io.scheduleSilence(SILENCE_MS)
            }
            VoiceState.THINKING, VoiceState.SPEAKING -> {
                if (!policy.listenWhileSpeaking(output)) return
                if (!SpeechText.isSelfOrEcho(t, "$spoken $lastSubmitted")) bargeIn(t, final)
            }
            else -> Unit
        }
    }

    /** The end-of-utterance countdown ran out: send what was heard. */
    fun onSilence() {
        if (state != VoiceState.LISTENING || heard.isBlank()) return
        lastSubmitted = heard
        io.sendTurn(heard)
        heard = ""
        finals = ""
        spoken = ""
        replyEnded = false
        queued = 0
        sentences.clear()
        enter(VoiceState.THINKING)
    }

    /** One event of the reply stream. Reasoning is never spoken (ADR 0071). */
    fun onReply(event: StreamEvent) {
        if (state != VoiceState.THINKING && state != VoiceState.SPEAKING) return
        when (event.kind) {
            StreamEventType.TEXT_DELTA -> sentences.push(event.text.orEmpty())?.let(::say)
            // A research loop's later iteration supersedes what came before (ADR 0082): stop reading the old text.
            StreamEventType.TEXT_RESET -> {
                sentences.clear()
                io.stopSpeaking()
                queued = 0
                spoken = ""
                if (state == VoiceState.SPEAKING) enter(VoiceState.THINKING)
            }
            else -> Unit
        }
    }

    /** The reply stream ended (done, stopped, or dropped): speak the tail, then listen once it is all spoken. */
    fun onReplyEnded() {
        if (state != VoiceState.THINKING && state != VoiceState.SPEAKING) return
        sentences.flush()?.let(::say)
        replyEnded = true
        if (queued == 0) enter(VoiceState.LISTENING)
    }

    /** The speaker finished one queued sentence. */
    fun onSentenceSpoken() {
        if (queued > 0) queued--
        if (queued == 0 && replyEnded && state == VoiceState.SPEAKING) enter(VoiceState.LISTENING)
    }

    private fun say(text: String) {
        val clean = SpeechText.stripMarkdown(text)
        if (clean.isEmpty()) return
        // The echo reference is extended BEFORE playback, so a barge-in can compare against it.
        spoken = "$spoken $clean".trim()
        queued++
        io.speak(clean)
        if (state == VoiceState.THINKING) enter(VoiceState.SPEAKING)
    }

    private fun bargeIn(text: String, final: Boolean) {
        // Both halves: the local stop makes it immediate; the server stop keeps the stored turn honest (ADR 0078).
        io.stopSpeaking()
        io.stopTurn()
        sentences.clear()
        queued = 0
        spoken = ""
        finals = if (final) text else ""
        heard = text
        enter(VoiceState.LISTENING)
        io.scheduleSilence(SILENCE_MS)
    }

    private fun enter(next: VoiceState) {
        state = next
        updateMic()
        io.stateChanged(next)
    }

    private fun updateMic() {
        val live = when (state) {
            VoiceState.LISTENING -> !muted
            VoiceState.THINKING, VoiceState.SPEAKING -> !muted && policy.listenWhileSpeaking(output)
            else -> false
        }
        io.listen(live)
    }

    companion object {
        /** End-of-utterance debounce, as voice.js SILENCE_MS. */
        const val SILENCE_MS = 1200L
    }
}
