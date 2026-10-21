package nl.agent42.android.voice

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import java.util.Locale

/**
 * Continuous listening over the platform recognizer (ADR 0114). The DEFAULT recognition service, not the on-device
 * one: on the owner's phone Dutch has no on-device model, so on-device fails every attempt with "language not
 * supported". That service sends audio to Google, the same egress as Chrome's Web Speech today (ADR 0006).
 *
 * The recognizer ends after each utterance; this restarts it for as long as listening is on. Its own end-of-speech
 * is NOT the end of the user's turn: [VoiceLoop] waits for 1.2 s of silence after the last result, as the web does.
 */
class PlatformSpeechInput(
    private val context: Context,
    private val onHeard: (text: String, final: Boolean) -> Unit,
    private val onUnavailable: (String) -> Unit,
) {
    private val main = Handler(Looper.getMainLooper())
    private var recognizer: SpeechRecognizer? = null
    private var on = false

    fun listen(value: Boolean) = main.post {
        if (value == on) return@post
        on = value
        if (value) start() else stopRecognizer()
    }

    fun release() = main.post {
        on = false
        stopRecognizer()
    }

    private fun start() {
        if (!on) return
        if (!SpeechRecognizer.isRecognitionAvailable(context)) {
            onUnavailable("Speech recognition isn't available on this phone.")
            return
        }
        val r = recognizer ?: SpeechRecognizer.createSpeechRecognizer(context).also {
            it.setRecognitionListener(listener)
            recognizer = it
        }
        r.startListening(
            Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                .putExtra(RecognizerIntent.EXTRA_LANGUAGE, Locale.getDefault().toLanguageTag())
                .putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true),
        )
    }

    private fun stopRecognizer() {
        recognizer?.destroy()
        recognizer = null
    }

    private fun restart(delayMs: Long) {
        main.postDelayed({ if (on) start() }, delayMs)
    }

    private val listener = object : RecognitionListener {
        override fun onPartialResults(partial: Bundle?) {
            first(partial)?.let { onHeard(it, false) }
        }

        override fun onResults(results: Bundle?) {
            first(results)?.let { onHeard(it, true) }
            restart(0)
        }

        override fun onError(error: Int) {
            when (error) {
                SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS -> onUnavailable("Microphone permission is off.")
                SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED, SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE ->
                    onUnavailable("This phone can't recognize ${Locale.getDefault().displayLanguage}.")
                // No match / timeout / server disconnected (which follows every result, measured) are just "keep going".
                else -> restart(if (error == SpeechRecognizer.ERROR_RECOGNIZER_BUSY) BUSY_RETRY_MS else 0)
            }
        }

        override fun onReadyForSpeech(params: Bundle?) {}
        override fun onBeginningOfSpeech() {}
        override fun onRmsChanged(rmsdB: Float) {}
        override fun onBufferReceived(buffer: ByteArray?) {}
        override fun onEndOfSpeech() {}
        override fun onEvent(eventType: Int, params: Bundle?) {}
    }

    private fun first(bundle: Bundle?): String? =
        bundle?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.takeIf { it.isNotBlank() }

    private companion object {
        const val BUSY_RETRY_MS = 300L
    }
}
