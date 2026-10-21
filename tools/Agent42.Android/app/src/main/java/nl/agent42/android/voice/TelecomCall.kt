package nl.agent42.android.voice

import android.content.Context
import android.media.AudioManager
import android.net.Uri
import android.telecom.DisconnectCause
import android.util.Log
import androidx.core.telecom.CallAttributesCompat
import androidx.core.telecom.CallControlScope
import androidx.core.telecom.CallEndpointCompat
import androidx.core.telecom.CallsManager
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

/**
 * The voice session as a self-managed Telecom call (androidx core-telecom, ADR 0114 phase 2). Telecom owns the audio
 * route, so this replaces [AudioRoute]: the app never calls setCommunicationDevice during a Telecom call (the library's
 * docs: doing so breaks call audio). In return the call shows in the system call UI and on the lock screen, car and
 * headset buttons hang it up, and a real incoming call puts it on hold.
 */
class TelecomCall(context: Context, private val listener: Listener) {
    // Its own scope: the disconnect must still go out after the voice session's scope is cancelled on hang-up.
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    interface Listener {
        fun onOutput(output: AudioOutput)
        fun onSystemMute(muted: Boolean)
        fun onHold(held: Boolean)
        fun onDisconnected()
        fun onFailed(error: Throwable)
    }

    private val calls = CallsManager(context)
    private val audio = context.getSystemService(AudioManager::class.java)
    private var control: CallControlScope? = null
    private var job: Job? = null
    private var current: CallEndpointCompat? = null
    private var endpoints: List<CallEndpointCompat> = emptyList()
    private var ended = false

    fun start(title: String, conversationId: String) {
        job = scope.launch {
            try {
                calls.registerAppWithTelecom(CallsManager.CAPABILITY_BASELINE)
                val starting = withTimeoutOrNull(STARTING_ENDPOINTS_MS) { calls.getAvailableStartingCallEndpoints().first() }.orEmpty()
                val preferred = CallEndpoints.preferredStart(starting.map { it.type })?.let { type -> starting.first { it.type == type } }
                val attributes = CallAttributesCompat(
                    "Agent 42 · $title",
                    Uri.parse("agent42:$conversationId"),
                    CallAttributesCompat.DIRECTION_OUTGOING,
                    CallAttributesCompat.CALL_TYPE_AUDIO_CALL,
                    CallAttributesCompat.SUPPORTS_SET_INACTIVE,
                    preferred,
                )
                calls.addCall(
                    attributes,
                    onAnswer = { },
                    onDisconnect = { cause ->
                        Log.i(VoiceSession.TAG, "telecom: disconnected by the system ($cause)")
                        ended = true
                        listener.onDisconnected()
                    },
                    onSetActive = { listener.onHold(false) },
                    onSetInactive = { listener.onHold(true) },
                ) {
                    control = this
                    launch {
                        val result = setActive()
                        Log.i(VoiceSession.TAG, "telecom: setActive=$result audioMode=${audio.mode}")
                        // The echo result (ADR 0114) was measured in MODE_IN_COMMUNICATION. The mode is not routing, which
                        // is Telecom's; set it only if the platform didn't.
                        if (audio.mode != AudioManager.MODE_IN_COMMUNICATION) audio.mode = AudioManager.MODE_IN_COMMUNICATION
                    }
                    launch {
                        currentCallEndpoint.collect {
                            current = it
                            Log.i(VoiceSession.TAG, "telecom: endpoint=${it.name} type=${it.type} audioMode=${audio.mode}")
                            listener.onOutput(CallEndpoints.output(it.type))
                        }
                    }
                    launch { availableEndpoints.collect { endpoints = it } }
                    launch { isMuted.collect { listener.onSystemMute(it) } }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                Log.w(VoiceSession.TAG, "telecom: addCall failed, falling back to the plain audio route", e)
                listener.onFailed(e)
            }
        }
    }

    fun toggleSpeaker() {
        val from = current ?: return
        val target = CallEndpoints.toggleTarget(from.type, endpoints.map { it.type }) ?: return
        val endpoint = endpoints.first { it.type == target }
        scope.launch { control?.requestEndpointChange(endpoint) }
    }

    fun hangUp() {
        if (ended) return
        ended = true
        val scopeControl = control
        scope.launch {
            withContext(NonCancellable) { scopeControl?.disconnect(DisconnectCause(DisconnectCause.LOCAL)) }
            job?.cancel()
            scope.cancel()
        }
    }

    private companion object {
        const val STARTING_ENDPOINTS_MS = 1_000L
    }
}
