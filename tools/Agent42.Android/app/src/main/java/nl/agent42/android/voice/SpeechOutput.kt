package nl.agent42.android.voice

import android.content.Context
import android.media.AudioAttributes
import android.media.MediaDataSource
import android.media.MediaPlayer
import android.os.Bundle
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.resume
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import nl.agent42.android.api.Agent42Api

/**
 * Reads the reply aloud, sentence by sentence, in order. Each sentence's audio is fetched from `/api/speech`
 * (the user's speech model, ADR 0038) the moment it is queued, so fetches overlap playback, as in voice.js.
 * A 409 (no speech model) switches this session to the platform TextToSpeech, preferring an offline voice.
 *
 * Everything plays with USAGE_VOICE_COMMUNICATION: that is what puts the reply on the call path, where the phone's
 * echo canceller has it as reference and the mic can stay live on the speaker (ADR 0114).
 */
class SpeechOutput(
    context: Context,
    private val scope: CoroutineScope,
    private val api: () -> Agent42Api,
    private val onSentenceSpoken: () -> Unit,
) {
    private class Item(val text: String, val audio: Deferred<ByteArray?>)

    private val attributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
        .build()

    @Volatile private var serverVoice = true
    private var queue = Channel<Item>(Channel.UNLIMITED)
    private var pump: Job = startPump()
    private var player: MediaPlayer? = null
    private val ttsDone = ConcurrentHashMap<String, CancellableContinuation<Unit>>()
    private var ttsReady = false
    private val tts = TextToSpeech(context) { status ->
        if (status == TextToSpeech.SUCCESS) configureTts()
    }

    fun speak(text: String) {
        val audio = scope.async(Dispatchers.IO) {
            if (!serverVoice) return@async null
            // null = 409, no speech model: the platform voice for the rest of this call. A failure = this sentence only.
            runCatching { api().speech(text) }.fold(
                onSuccess = { bytes -> bytes.also { if (it == null) serverVoice = false } },
                onFailure = { null },
            )
        }
        queue.trySend(Item(text, audio))
    }

    /** Stops mid-word and drops everything queued (barge-in, text reset, hang-up). */
    fun stop() {
        pump.cancel()
        queue.close()
        while (true) queue.tryReceive().getOrNull()?.audio?.cancel() ?: break
        player?.release()
        player = null
        tts.stop()
        ttsDone.values.forEach { if (it.isActive) it.resume(Unit) }
        ttsDone.clear()
        queue = Channel(Channel.UNLIMITED)
        pump = startPump()
    }

    fun release() {
        stop()
        pump.cancel()
        tts.shutdown()
    }

    private fun startPump(): Job = scope.launch(Dispatchers.Main) {
        for (item in queue) {
            val bytes = item.audio.await()
            if (bytes != null) playMp3(bytes) else speakLocally(item.text)
            onSentenceSpoken()
        }
    }

    private suspend fun playMp3(bytes: ByteArray) = suspendCancellableCoroutine { cont ->
        val p = MediaPlayer()
        player = p
        p.setAudioAttributes(attributes)
        p.setDataSource(BytesDataSource(bytes))
        p.setOnCompletionListener { finish(p, cont) }
        p.setOnErrorListener { _, _, _ -> finish(p, cont); true }
        p.setOnPreparedListener { it.start() }
        cont.invokeOnCancellation { scope.launch(Dispatchers.Main) { finish(p, null) } }
        p.prepareAsync()
    }

    private fun finish(p: MediaPlayer, cont: CancellableContinuation<Unit>?) {
        if (player === p) player = null
        runCatching { p.release() }
        if (cont?.isActive == true) cont.resume(Unit)
    }

    private suspend fun speakLocally(text: String) {
        if (!ttsReady) return
        suspendCancellableCoroutine { cont ->
            val id = "s${System.nanoTime()}"
            ttsDone[id] = cont
            cont.invokeOnCancellation { ttsDone.remove(id) }
            if (tts.speak(text, TextToSpeech.QUEUE_ADD, Bundle(), id) != TextToSpeech.SUCCESS) {
                ttsDone.remove(id)
                cont.resume(Unit)
            }
        }
    }

    private fun configureTts() {
        tts.setAudioAttributes(attributes)
        val locale = Locale.getDefault()
        tts.language = locale
        // An offline voice keeps the reply text on the phone; a network voice would send it to the engine's cloud.
        tts.voices.orEmpty()
            .filter { it.locale.language == locale.language && !it.isNetworkConnectionRequired }
            .minByOrNull { if (it.locale.country == locale.country) 0 else 1 }
            ?.let { tts.voice = it }
        tts.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
            override fun onStart(utteranceId: String?) {}
            override fun onDone(utteranceId: String?) = done(utteranceId)

            @Deprecated("Deprecated in Java")
            override fun onError(utteranceId: String?) = done(utteranceId)
            override fun onError(utteranceId: String?, errorCode: Int) = done(utteranceId)
        })
        ttsReady = true
    }

    private fun done(id: String?) {
        id?.let { ttsDone.remove(it) }?.let { if (it.isActive) it.resume(Unit) }
    }

    private class BytesDataSource(private val bytes: ByteArray) : MediaDataSource() {
        override fun readAt(position: Long, buffer: ByteArray, offset: Int, size: Int): Int {
            if (position >= bytes.size) return -1
            val n = minOf(size, bytes.size - position.toInt())
            System.arraycopy(bytes, position.toInt(), buffer, offset, n)
            return n
        }

        override fun getSize(): Long = bytes.size.toLong()
        override fun close() {}
    }
}
