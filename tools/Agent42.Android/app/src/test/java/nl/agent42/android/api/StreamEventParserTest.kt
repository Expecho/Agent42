package nl.agent42.android.api

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class StreamEventParserTest {

    // The golden file the server's AndroidStreamContractTests pins to the real serializer.
    private val fixture = File(System.getProperty("agent42.contract") ?: "../contract/stream-events.ndjson")

    private fun fixtureEvents() = fixture.readLines().mapNotNull(StreamEventParser::parse)

    @Test
    fun everyFixtureLineParsesToAKnownType() {
        val kinds = fixtureEvents().map { it.kind }

        assertTrue("fixture has an event this app doesn't know: $kinds", StreamEventType.UNKNOWN !in kinds)
        assertEquals(StreamEventType.entries.filter { it != StreamEventType.UNKNOWN }.toSet(), kinds.toSet())
    }

    @Test
    fun fixtureFieldsLandOnTheirProperties() {
        val byKind = fixtureEvents().associateBy { it.kind }

        assertEquals("Hello", byKind.getValue(StreamEventType.TEXT_DELTA).text)
        val start = byKind.getValue(StreamEventType.TOOL_CALL_START)
        assertEquals("get_current_location", start.toolName)
        assertEquals(true, start.clientTool)
        val done = byKind.getValue(StreamEventType.DONE)
        assertEquals(true, done.stopped)
        assertEquals(120L, done.inputTokens)
        assertEquals(0.0012, done.costUsd!!, 1e-9)
        assertEquals(listOf(ResearchTodo(1, "Search", true)), byKind.getValue(StreamEventType.TODOS).todos)
        assertEquals(3L, byKind.getValue(StreamEventType.STATE_SNAPSHOT).stateVersion)
    }

    @Test
    fun unknownTypeAndUnknownFieldsAreTolerated() {
        val event = StreamEventParser.parse("""{"type":"somethingNew","text":"x","brandNewField":42}""")!!

        assertEquals(StreamEventType.UNKNOWN, event.kind)
        assertEquals("x", event.text)
    }

    @Test
    fun blankLineIsSkipped() {
        assertNull(StreamEventParser.parse(""))
        assertNull(StreamEventParser.parse("   "))
    }
}
