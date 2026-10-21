package nl.agent42.android.voice

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFocusRequest
import android.media.AudioManager

/**
 * Puts the phone in call audio mode for the voice session (ADR 0114): MODE_IN_COMMUNICATION, transient audio focus
 * (music ducks or pauses), and a communication device chosen headset-first: Bluetooth, then wired, then the
 * loudspeaker (or the earpiece when the user switched to it). Everything is restored on [end].
 */
class AudioRoute(context: Context, private val onChanged: (AudioOutput) -> Unit) {
    private val audio = context.getSystemService(AudioManager::class.java)
    private val executor = context.mainExecutor
    private var preferEarpiece = false
    private val focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT)
        .setAudioAttributes(
            AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                .build(),
        )
        .build()
    private val deviceListener = AudioManager.OnCommunicationDeviceChangedListener { device ->
        onChanged(classify(device))
    }

    fun begin(): AudioOutput {
        audio.requestAudioFocus(focus)
        audio.mode = AudioManager.MODE_IN_COMMUNICATION
        audio.addOnCommunicationDeviceChangedListener(executor, deviceListener)
        return choose()
    }

    /** Loudspeaker ⇄ earpiece; a connected headset always wins. */
    fun toggleSpeaker(): AudioOutput {
        preferEarpiece = !preferEarpiece
        return choose()
    }

    fun end() {
        runCatching { audio.removeOnCommunicationDeviceChangedListener(deviceListener) }
        audio.clearCommunicationDevice()
        audio.mode = AudioManager.MODE_NORMAL
        audio.abandonAudioFocusRequest(focus)
    }

    private fun choose(): AudioOutput {
        val devices = audio.availableCommunicationDevices
        val pick = devices.firstOrNull { it.type in BLUETOOTH }
            ?: devices.firstOrNull { it.type in WIRED }
            ?: devices.firstOrNull { it.type == if (preferEarpiece) AudioDeviceInfo.TYPE_BUILTIN_EARPIECE else AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
        pick?.let { audio.setCommunicationDevice(it) }
        return classify(pick)
    }

    private fun classify(device: AudioDeviceInfo?): AudioOutput = when (device?.type) {
        in BLUETOOTH -> AudioOutput.BLUETOOTH
        in WIRED -> AudioOutput.WIRED_HEADSET
        AudioDeviceInfo.TYPE_BUILTIN_EARPIECE -> AudioOutput.EARPIECE
        else -> AudioOutput.SPEAKER
    }

    private companion object {
        val BLUETOOTH = setOf(AudioDeviceInfo.TYPE_BLUETOOTH_SCO, AudioDeviceInfo.TYPE_BLE_HEADSET, AudioDeviceInfo.TYPE_BLE_SPEAKER)
        val WIRED = setOf(AudioDeviceInfo.TYPE_WIRED_HEADSET, AudioDeviceInfo.TYPE_WIRED_HEADPHONES, AudioDeviceInfo.TYPE_USB_HEADSET)
    }
}
