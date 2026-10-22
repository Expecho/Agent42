package nl.agent42.android.chat

import nl.agent42.android.api.ChatToolCall
import nl.agent42.android.api.ChatTurn
import nl.agent42.android.api.StreamEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ChatReducerTest {
    private val live = UiTurn(Role.ASSISTANT, complete = false)

    private fun UiTurn.apply(vararg events: StreamEvent) = events.fold(this, ChatReducer::reduce)

    @Test
    fun deltasAppend_andReasoningStaysSeparate() {
        val turn = live.apply(
            StreamEvent("reasoning", text = "Let me think"),
            StreamEvent("textDelta", text = "Hel"),
            StreamEvent("textDelta", text = "lo"),
        )

        assertEquals("Hello", turn.text)
        assertEquals("Let me think", turn.reasoning)
    }

    @Test
    fun textResetClearsTheText_butKeepsTheToolCalls() {
        val turn = live.apply(
            StreamEvent("textDelta", text = "draft"),
            StreamEvent("toolCallStart", toolName = "web_search", toolCallId = "c1"),
            StreamEvent("textReset"),
            StreamEvent("textDelta", text = "final"),
        )

        assertEquals("final", turn.text)
        assertEquals(listOf("c1"), turn.tools.map { it.id })
    }

    @Test
    fun aToolCallEndCompletesItsStart() {
        val turn = live.apply(
            StreamEvent("toolCallStart", toolName = "web_search", toolCallId = "c1", toolInput = "{}"),
            StreamEvent("toolCallEnd", toolName = "web_search", toolCallId = "c1", toolResult = "3 results"),
        )

        assertEquals(listOf(UiTool("c1", "web_search", "{}", "3 results", done = true)), turn.tools)
    }

    @Test
    fun doneCompletesTheTurn_andMarksAStoppedOne() {
        assertTrue(live.apply(StreamEvent("done")).complete)
        assertFalse(live.apply(StreamEvent("done")).stopped)
        assertTrue(live.apply(StreamEvent("done", stopped = true)).stopped)
    }

    @Test
    fun anUnknownEventChangesNothing() {
        val turn = live.apply(StreamEvent("textDelta", text = "x"))

        assertEquals(turn, turn.apply(StreamEvent("somethingNew", text = "ignored")))
    }

    @Test
    fun historyMapsRolesAndCompletedToolCalls() {
        val turns = ChatReducer.fromHistory(
            listOf(
                ChatTurn("user", "hi"),
                ChatTurn("assistant", "hello", toolCalls = listOf(ChatToolCall("c1", "web_search", "{}", "ok"))),
            ),
        )

        assertEquals(listOf(Role.USER, Role.ASSISTANT), turns.map { it.role })
        assertEquals(listOf(UiTool("c1", "web_search", "{}", "ok", done = true)), turns[1].tools)
        assertTrue(turns.all { it.complete })
    }
}
