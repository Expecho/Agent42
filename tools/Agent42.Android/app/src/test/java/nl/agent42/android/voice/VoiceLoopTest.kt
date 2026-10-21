package nl.agent42.android.voice

import nl.agent42.android.api.StreamEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VoiceLoopTest {
    private class FakeIo : VoiceIo {
        val calls = mutableListOf<String>()
        var mic = false
        override fun listen(on: Boolean) { mic = on }
        override fun sendTurn(text: String) { calls += "send:$text" }
        override fun speak(sentence: String) { calls += "speak:$sentence" }
        override fun stopSpeaking() { calls += "stopSpeaking" }
        override fun stopTurn() { calls += "stopTurn" }
        override fun scheduleSilence(delayMs: Long) { calls += "silence" }
        override fun cancelSilence() {}
        override fun stateChanged(state: VoiceState) {}
    }

    private val io = FakeIo()
    private val loop = VoiceLoop(io).apply { start() }

    private fun ask(text: String) {
        loop.onHeard(text, final = true)
        loop.onSilence()
    }

    @Test
    fun anUtteranceIsSentAfterSilence_andTheReplyIsSpokenSentenceBySentence() {
        loop.onHeard("wat voor weer", final = false)
        loop.onHeard("wat voor weer wordt het morgen", final = true)
        assertTrue(io.calls.contains("silence"))
        loop.onSilence()
        assertEquals(VoiceState.THINKING, loop.state)
        assertTrue(io.calls.contains("send:wat voor weer wordt het morgen"))

        loop.onReply(StreamEvent("textDelta", text = "Morgen **regent** het. Neem een"))
        assertEquals(VoiceState.SPEAKING, loop.state)
        assertTrue(io.calls.contains("speak:Morgen regent het."))

        loop.onReply(StreamEvent("textDelta", text = " jas mee"))
        loop.onReplyEnded()
        assertTrue(io.calls.contains("speak:Neem een jas mee"))
        loop.onSentenceSpoken()
        assertEquals(VoiceState.SPEAKING, loop.state)
        loop.onSentenceSpoken()
        assertEquals(VoiceState.LISTENING, loop.state)
    }

    @Test
    fun reasoningIsNeverSpoken() {
        ask("hallo")
        loop.onReply(StreamEvent("reasoning", text = "The user greets me."))

        assertFalse(io.calls.any { it.startsWith("speak:") })
    }

    @Test
    fun talkingOverTheReplyStopsPlaybackAndTheTurn_andStartsTheNextUtterance() {
        ask("vertel een lang verhaal")
        loop.onReply(StreamEvent("textDelta", text = "Er was eens een koning."))

        loop.onHeard("stop even", final = false)

        assertTrue(io.calls.containsAll(listOf("stopSpeaking", "stopTurn")))
        assertEquals(VoiceState.LISTENING, loop.state)
        assertEquals("stop even", loop.heard)
    }

    @Test
    fun hearingItsOwnReplyIsNotABargeIn() {
        ask("vertel een verhaal")
        loop.onReply(StreamEvent("textDelta", text = "Er was eens een koning."))

        loop.onHeard("er was eens een koning", final = true)

        assertFalse(io.calls.contains("stopTurn"))
        assertEquals(VoiceState.SPEAKING, loop.state)
    }

    @Test
    fun textResetDropsWhatWasQueued() {
        ask("onderzoek dit")
        loop.onReply(StreamEvent("textDelta", text = "Eerste poging."))
        loop.onReply(StreamEvent("textReset"))

        assertTrue(io.calls.contains("stopSpeaking"))
        assertEquals(VoiceState.THINKING, loop.state)
        assertEquals("", loop.spoken)
    }

    @Test
    fun anEmptyReplyGoesStraightBackToListening() {
        ask("hallo")
        loop.onReplyEnded()

        assertEquals(VoiceState.LISTENING, loop.state)
    }

    @Test
    fun theMicStaysLiveWhileSpeakingOnTheSpeaker_unlessThePolicySaysHalfDuplex() {
        ask("hallo")
        assertTrue(io.mic)

        loop.policy = DuplexPolicy(fullDuplexOnSpeaker = false)
        loop.outputChanged(AudioOutput.SPEAKER)
        assertFalse(io.mic)
        loop.onHeard("stop", final = true)
        assertFalse(io.calls.contains("stopTurn"))

        loop.outputChanged(AudioOutput.BLUETOOTH)
        assertTrue(io.mic)
    }

    @Test
    fun mutingClosesTheMic_andHangingUpMidReplyStopsTheTurn() {
        loop.setMuted(true)
        assertFalse(io.mic)
        loop.setMuted(false)
        ask("hallo")
        loop.hangUp()

        assertTrue(io.calls.containsAll(listOf("stopSpeaking", "stopTurn")))
        assertEquals(VoiceState.ENDED, loop.state)
        assertFalse(io.mic)
    }

    @Test
    fun speechTextRules() {
        assertEquals("Zie de docs nu", SpeechText.stripMarkdown("Zie [de docs](https://x) ```code``` nu"))
        assertTrue(SpeechText.isSelfOrEcho("Er was eens", "er was eens een koning"))
        assertFalse(SpeechText.isSelfOrEcho("stop even", "er was eens een koning"))
        val buffer = SentenceBuffer()
        assertEquals(null, buffer.push("Hallo"))
        assertEquals("Hallo daar.", buffer.push(" daar. En"))
        assertEquals(" en", buffer.flush()?.lowercase())
    }
}
