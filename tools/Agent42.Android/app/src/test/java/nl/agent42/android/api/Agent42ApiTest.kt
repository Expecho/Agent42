package nl.agent42.android.api

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.JsonPrimitive
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okio.Buffer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class Agent42ApiTest {
    private val server = MockWebServer()
    private lateinit var api: Agent42Api

    @Before
    fun setUp() {
        server.start()
        api = Agent42Api(server.url("/").toString(), SessionCookie("abc"))
    }

    @After
    fun tearDown() {
        server.close()
    }

    @Test
    fun chatStreamsEveryLine_evenWhenALineIsSplitAcrossReads() = runTest {
        val body = """{"type":"textDelta","text":"Hel"}""" + "\n" + """{"type":"textDelta","text":"lo"}""" + "\n\n" +
            """{"type":"done","inputTokens":3}""" + "\n"
        // 7-byte chunks cut every line in the middle.
        server.enqueue(MockResponse.Builder().chunkedBody(body, 7).build())

        val events = api.chat("c1", ChatRequest("hi")).toList()

        assertEquals(listOf("Hel", "lo"), events.filter { it.kind == StreamEventType.TEXT_DELTA }.map { it.text })
        assertEquals(StreamEventType.DONE, events.last().kind)
        assertEquals(3, events.size)
    }

    @Test
    fun chatRequestCarriesTheAdvertisedClientTools_andOmitsNulls() = runTest {
        server.enqueue(MockResponse.Builder().body("").build())

        api.chat("c1", ChatRequest("hi", clientTools = listOf("get_current_location"))).toList()

        val recorded = server.takeRequest()
        assertEquals("/api/chat/c1", recorded.url.encodedPath)
        assertEquals("""{"message":"hi","clientTools":["get_current_location"]}""", recorded.body!!.utf8())
        assertEquals("AppServiceAuthSession=abc", recorded.headers["Cookie"])
    }

    @Test
    fun aRedirectToTheLoginPageIsSignedOut_notFollowed() = runTest {
        server.enqueue(
            MockResponse.Builder().code(302).addHeader("Location", "https://login.microsoftonline.com/x").build(),
        )

        assertThrows(SignedOutException::class.java) { kotlinx.coroutines.runBlocking { api.conversations() } }
        assertEquals(1, server.requestCount)
    }

    @Test
    fun anHtmlPageOrA401IsSignedOut() = runTest {
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/html; charset=utf-8").body("<html/>").build())
        server.enqueue(MockResponse.Builder().code(401).build())

        assertThrows(SignedOutException::class.java) { kotlinx.coroutines.runBlocking { api.conversations() } }
        assertThrows(SignedOutException::class.java) { kotlinx.coroutines.runBlocking { api.models() } }
    }

    @Test
    fun speechWithoutASpeechModelIsNull_soTheCallerFallsBackToThePlatformVoice() = runTest {
        server.enqueue(MockResponse.Builder().code(409).body("No speech model").build())
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "audio/mpeg").body(Buffer().write(byteArrayOf(1, 2, 3))).build())

        assertNull(api.speech("Hello."))
        assertEquals(3, api.speech("Hello.")!!.size)
    }

    @Test
    fun toolResultIsFalseWhenNoCallWasWaiting() = runTest {
        server.enqueue(MockResponse.Builder().code(204).build())
        server.enqueue(MockResponse.Builder().code(404).build())

        assertTrue(api.toolResult("c1", "call_1", JsonPrimitive("ok")))
        assertFalse(api.toolResult("c1", "call_1", JsonPrimitive("ok")))
        assertEquals("""{"toolCallId":"call_1","result":"ok"}""", server.takeRequest().body!!.utf8())
    }

    @Test
    fun anImageUploadReturnsItsId_aDocumentUploadHasNone() = runTest {
        server.enqueue(MockResponse.Builder().body("""{"id":"att_1","mediaType":"image/png","fileName":"a.png"}""").build())
        server.enqueue(
            MockResponse.Builder()
                .body("""{"kind":"document","fileName":"a.pdf","mediaType":"application/pdf","chunkCount":4}""").build(),
        )

        assertEquals(UploadResult.Image("att_1", "image/png", "a.png"), api.upload("c1", byteArrayOf(1), "a.png", "image/png"))
        assertEquals(UploadResult.Document("a.pdf", 4), api.upload("c1", byteArrayOf(1), "a.pdf", "application/pdf"))
        assertTrue(server.takeRequest().body!!.utf8().contains("name=\"file\"; filename=\"a.png\""))
    }

    @Test
    fun anUploadOver10MbIsRefusedBeforeItIsSent() = runTest {
        assertThrows(IllegalArgumentException::class.java) {
            kotlinx.coroutines.runBlocking {
                api.upload("c1", ByteArray(Agent42Api.MAX_ATTACHMENT_BYTES + 1), "big.png", "image/png")
            }
        }
        assertEquals(0, server.requestCount)
    }

    @Test
    fun theModelPickerOffersChatModelsOnly() {
        val chat = ModelInfo("gpt", "azure")
        val copilot = ModelInfo("c", "github", copilotOnly = true)
        val speech = ModelInfo("tts", "azure", supportsSpeech = true)
        val transcription = ModelInfo("stt", "azure", supportsTranscription = true)

        assertEquals(listOf(chat), listOf(chat, copilot, speech, transcription).filter { it.isChatModel })
    }
}
