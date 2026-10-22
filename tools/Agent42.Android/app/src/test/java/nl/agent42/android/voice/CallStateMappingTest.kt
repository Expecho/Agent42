package nl.agent42.android.voice

import androidx.core.telecom.CallEndpointCompat.Companion.TYPE_BLUETOOTH
import androidx.core.telecom.CallEndpointCompat.Companion.TYPE_EARPIECE
import androidx.core.telecom.CallEndpointCompat.Companion.TYPE_SPEAKER
import androidx.core.telecom.CallEndpointCompat.Companion.TYPE_WIRED_HEADSET
import nl.agent42.android.api.StreamEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CallStateMappingTest {
    private class FakeIo : VoiceIo {
        val calls = mutableListOf<String>()
        var mic = false
        override fun listen(on: Boolean) { mic = on }
        override fun sendTurn(text: String) { calls += "send:$text" }
        override fun speak(sentence: String) { calls += "speak:$sentence" }
        override fun stopSpeaking() { calls += "stopSpeaking" }
        override fun stopTurn() { calls += "stopTurn" }
        override fun scheduleSilence(delayMs: Long) {}
        override fun cancelSilence() {}
        override fun stateChanged(state: VoiceState) {}
    }

    private val io = FakeIo()
    private val loop = VoiceLoop(io).apply { start() }

    @Test
    fun holdSilencesAndClosesTheMic_resumeReopensIt() {
        loop.onHeard("vertel een verhaal", final = true)
        loop.onSilence()
        loop.onReply(StreamEvent("textDelta", text = "Er was eens."))

        loop.hold(true)
        assertTrue(io.calls.contains("stopSpeaking"))
        assertFalse(io.mic)
        loop.onReply(StreamEvent("textDelta", text = " Een koning."))
        assertEquals(1, io.calls.count { it.startsWith("speak:") })
        loop.onReplyEnded()
        assertEquals(VoiceState.LISTENING, loop.state)

        loop.hold(false)
        assertTrue(io.mic)
    }

    @Test
    fun disconnectEndsTheCall() {
        loop.hangUp()
        assertEquals(VoiceState.ENDED, loop.state)
    }

    @Test
    fun aCallStartsOnAHeadsetWhenThereIsOne_elseTheLoudspeaker() {
        assertEquals(TYPE_BLUETOOTH, CallEndpoints.preferredStart(listOf(TYPE_EARPIECE, TYPE_SPEAKER, TYPE_BLUETOOTH)))
        assertEquals(TYPE_WIRED_HEADSET, CallEndpoints.preferredStart(listOf(TYPE_EARPIECE, TYPE_WIRED_HEADSET, TYPE_SPEAKER)))
        assertEquals(TYPE_SPEAKER, CallEndpoints.preferredStart(listOf(TYPE_EARPIECE, TYPE_SPEAKER)))
    }

    @Test
    fun theSpeakerButtonTogglesLoudspeakerAndEarpiece_only() {
        val phone = listOf(TYPE_EARPIECE, TYPE_SPEAKER)
        assertEquals(TYPE_EARPIECE, CallEndpoints.toggleTarget(TYPE_SPEAKER, phone))
        assertEquals(TYPE_SPEAKER, CallEndpoints.toggleTarget(TYPE_EARPIECE, phone))
        assertNull(CallEndpoints.toggleTarget(TYPE_BLUETOOTH, phone + TYPE_BLUETOOTH))
        assertEquals(AudioOutput.BLUETOOTH, CallEndpoints.output(TYPE_BLUETOOTH))
    }
}
