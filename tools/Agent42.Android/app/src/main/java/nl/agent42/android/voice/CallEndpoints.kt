package nl.agent42.android.voice

import androidx.core.telecom.CallEndpointCompat

/** Telecom endpoint types (ints, so this is unit-testable) mapped to the loop's outputs, and the starting choice. */
object CallEndpoints {

    fun output(type: Int): AudioOutput = when (type) {
        CallEndpointCompat.TYPE_BLUETOOTH -> AudioOutput.BLUETOOTH
        CallEndpointCompat.TYPE_WIRED_HEADSET -> AudioOutput.WIRED_HEADSET
        CallEndpointCompat.TYPE_EARPIECE -> AudioOutput.EARPIECE
        else -> AudioOutput.SPEAKER
    }

    /**
     * Where a call starts: a headset when one is connected (Bluetooth, then wired), else the loudspeaker. A phone call
     * would start on the earpiece, but a hands-free assistant call is held at arm's length or lies on the table.
     */
    fun preferredStart(types: List<Int>): Int? =
        listOf(CallEndpointCompat.TYPE_BLUETOOTH, CallEndpointCompat.TYPE_WIRED_HEADSET, CallEndpointCompat.TYPE_SPEAKER)
            .firstOrNull { it in types }

    /** The speaker button: loudspeaker ⇄ earpiece. With a headset connected it does nothing. */
    fun toggleTarget(current: Int, types: List<Int>): Int? = when (current) {
        CallEndpointCompat.TYPE_SPEAKER -> CallEndpointCompat.TYPE_EARPIECE.takeIf { it in types }
        CallEndpointCompat.TYPE_EARPIECE -> CallEndpointCompat.TYPE_SPEAKER.takeIf { it in types }
        else -> null
    }
}
