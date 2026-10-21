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

    /** Diagnostics: every decision the loop takes, for logcat. */
    fun trace(message: String) {}
}

/**
 * The hands-free conversation as a pure state machine (ADR 0037's loop, ported; ADR 0114's duplex rule). No Android
 * types: every effect goes through [VoiceIo], so the whole flow is unit-tested on the JVM.
 *
 * listening --(silence after speech)--> thinking --(first sentence)--> speaking --(reply spoken)--> listening
 * Talking over thinking/speaking (when the mic is live) is a barge-in: stop the audio, stop the turn on the server
 * (ADR 0078), and the interrupting words start the next utterance.
 */
class VoiceLoop(
    private val io: VoiceIo,
    var policy: DuplexPolicy = DuplexPolicy(),
    private val now: () -> Long = System::currentTimeMillis,
) {
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

    // The reply just spoken, and until when its tail may still come back from the recognizer as "heard" (measured:
    // the recognizer delivers what it picked up of the last sentence after playback ends, i.e. while listening).
    private var lastReply = ""
    private var echoGuardUntil = 0L
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

    /**
     * The system put the call on hold (a real phone call came in) or took it off hold. While held the mic is closed and
     * nothing is spoken; a reply still streaming is shown but not read aloud, and the loop returns to listening when it
     * ends. Resuming re-opens the mic.
     */
    var held = false
        private set

    fun hold(value: Boolean) {
        if (held == value) return
        held = value
        io.trace("hold=$value")
        if (value) {
            io.stopSpeaking()
            queued = 0
            if (state == VoiceState.SPEAKING) enter(VoiceState.THINKING)
            if (state == VoiceState.THINKING && replyEnded) enter(VoiceState.LISTENING)
        }
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
                if (now() < echoGuardUntil && SpeechText.isSelfOrEcho(t, "$lastReply $lastSubmitted")) {
                    io.trace("drop echo tail (final=$final): $t")
                    return
                }
                if (final) finals = "$finals $t".trim()
                heard = if (final) finals else "$finals $t".trim()
                io.trace("heard (final=$final): $heard")
                io.scheduleSilence(SILENCE_MS)
            }
            VoiceState.THINKING, VoiceState.SPEAKING -> {
                if (!policy.listenWhileSpeaking(output)) return
                when {
                    SpeechText.isSelfOrEcho(t, "$spoken $lastSubmitted") -> io.trace("drop echo while $state (final=$final): $t")
                    // A one-word partial is too thin to tell the user from a misheard scrap of the reply; wait for more.
                    !final && SpeechText.wordCount(t) < BARGE_IN_MIN_WORDS -> io.trace("wait, too short to barge in: $t")
                    else -> {
                        io.trace("BARGE-IN while $state (final=$final): $t")
                        bargeIn(t, final)
                    }
                }
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
        if (clean.isEmpty() || held) return
        // The echo reference is extended BEFORE playback, so a barge-in can compare against it.
        spoken = "$spoken $clean".trim()
        queued++
        io.speak(clean)
        if (state == VoiceState.THINKING) enter(VoiceState.SPEAKING)
    }

    private fun bargeIn(text: String, final: Boolean) {
        // Talking while it is still THINKING (nothing spoken yet) means the user wasn't done: they paused longer than the
        // silence window mid-sentence. The stopped turn saves nothing (a cancelled run persists nothing), so what was
        // sent goes back in front of what follows instead of being lost.
        val carried = if (state == VoiceState.THINKING && spoken.isEmpty()) "$lastSubmitted " else ""
        // Both halves: the local stop makes it immediate; the server stop keeps the stored turn honest (ADR 0078).
        io.stopSpeaking()
        io.stopTurn()
        sentences.clear()
        queued = 0
        spoken = ""
        finals = if (final) "$carried$text" else carried.trim()
        heard = "$carried$text"
        enter(VoiceState.LISTENING)
        io.scheduleSilence(SILENCE_MS)
    }

    private fun enter(next: VoiceState) {
        // Reply finished speaking on its own: guard the next moments against its tail coming back as "heard".
        if (state == VoiceState.SPEAKING && next == VoiceState.LISTENING) {
            lastReply = spoken
            echoGuardUntil = now() + ECHO_TAIL_MS
        }
        io.trace("state $state -> $next (output=$output, muted=$muted)")
        state = next
        updateMic()
        io.stateChanged(next)
    }

    private fun updateMic() {
        val live = !held && when (state) {
            VoiceState.LISTENING -> !muted
            VoiceState.THINKING, VoiceState.SPEAKING -> !muted && policy.listenWhileSpeaking(output)
            else -> false
        }
        io.listen(live)
    }

    companion object {
        /** End-of-utterance debounce, as voice.js SILENCE_MS. */
        const val SILENCE_MS = 1200L

        /** How long after the reply ends a result that echoes it is still dropped. */
        const val ECHO_TAIL_MS = 2500L

        /** A partial result needs this many words to interrupt the reply; a final always may. */
        const val BARGE_IN_MIN_WORDS = 2
    }
}
