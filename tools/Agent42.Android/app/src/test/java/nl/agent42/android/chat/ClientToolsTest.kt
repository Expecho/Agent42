package nl.agent42.android.chat

import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Test

class ClientToolsTest {

    @Test
    fun onlyTheForegroundChatScreenAdvertisesTools() {
        assertEquals(listOf("get_current_location", "confirm_action"), ClientTools.advertised(chatInForeground = true, voice = false))
        assertEquals(emptyList<String>(), ClientTools.advertised(chatInForeground = true, voice = true))
        assertEquals(emptyList<String>(), ClientTools.advertised(chatInForeground = false, voice = false))
    }

    // The same JSON the web client's location.js posts (toFix), so the model can't tell which client answered.
    @Test
    fun locationResultMatchesTheWebClientShape() {
        val fix = Fix(52.37, 4.89, 12.5, Instant.parse("2026-10-21T18:00:00Z"))

        assertEquals(
            """{"latitude":52.37,"longitude":4.89,"accuracyMetres":12.5,"capturedAt":"2026-10-21T18:00:00Z"}""",
            ClientTools.location(fix).toString(),
        )
        assertEquals(
            """{"latitude":1.0,"longitude":2.0,"accuracyMetres":null,"capturedAt":"2026-10-21T18:00:00Z"}""",
            ClientTools.location(Fix(1.0, 2.0, null, Instant.parse("2026-10-21T18:00:00Z"))).toString(),
        )
    }

    @Test
    fun statusAndConfirmResultsMatchTheWebClient() {
        assertEquals("""{"status":"not_shared"}""", ClientTools.NOT_SHARED.toString())
        assertEquals("""{"status":"no_fix"}""", ClientTools.NO_FIX.toString())
        assertEquals("""{"status":"unavailable"}""", ClientTools.UNAVAILABLE.toString())
        assertEquals("""{"approved":true}""", ClientTools.confirmed(true).toString())
    }

    @Test
    fun confirmPromptIsTheModelsAction_orAFallback() {
        assertEquals("Delete the file", ClientTools.confirmPrompt("""{"action":"Delete the file"}"""))
        assertEquals("The assistant wants to take an action.", ClientTools.confirmPrompt(null))
        assertEquals("The assistant wants to take an action.", ClientTools.confirmPrompt("not json"))
    }
}
