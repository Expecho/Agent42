package nl.agent42.android.api

import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.job
import kotlinx.coroutines.withContext
import kotlinx.serialization.KSerializer
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okio.BufferedSource

/** A non-2xx answer the caller didn't expect. */
class ApiException(val code: Int, message: String) : IOException(message)

/**
 * The Agent42 HTTP API, as this app uses it. One instance per signed-in base URL.
 *
 * The chat call has no read timeout: a turn streams for minutes, and an advertised `confirm_action` holds the stream
 * silent for up to 120 s while it waits for this client (ClientToolLimits.ConfirmTimeout).
 */
class Agent42Api(
    baseUrl: String,
    private val credentials: Credentials,
    client: OkHttpClient = OkHttpClient(),
) {
    private val base: HttpUrl = baseUrl.trimEnd('/').toHttpUrl()

    private val http: OkHttpClient = client.newBuilder()
        .followRedirects(false)
        .followSslRedirects(false)
        .addInterceptor(SignedOutInterceptor())
        .build()

    private val streaming: OkHttpClient = http.newBuilder()
        .readTimeout(java.time.Duration.ZERO)
        .callTimeout(java.time.Duration.ZERO)
        .build()

    suspend fun conversations(): List<ConversationInfo> =
        get("api/conversations", ListSerializer(ConversationInfo.serializer()))

    suspend fun messages(conversationId: String): List<ChatTurn> =
        get("api/conversations/$conversationId/messages", ListSerializer(ChatTurn.serializer()))

    suspend fun search(query: String): List<SearchHit> =
        get(url("api/search").newBuilder().addQueryParameter("q", query).build(), SearchResponse.serializer()).hits

    suspend fun models(): ModelCatalog = get("api/models", ModelCatalog.serializer())

    /** The conversation's current model id (the server answers a JSON string). */
    suspend fun conversationModel(conversationId: String): String =
        get("api/conversations/$conversationId/model", String.serializer())

    suspend fun pendingToolCalls(conversationId: String): List<PendingClientToolCall> =
        get("api/chat/$conversationId/pending-tool-calls", ListSerializer(PendingClientToolCall.serializer()))

    /** Stops the turn this conversation is streaming (ADR 0078). The server answers 204 either way. */
    suspend fun stop(conversationId: String) {
        send(post(url("api/chat/$conversationId/stop"), EMPTY_BODY)).close()
    }

    /**
     * Answers a client tool call. False when no call was waiting (404): it already timed out, or another client
     * answered it.
     */
    suspend fun toolResult(conversationId: String, toolCallId: String, result: JsonElement): Boolean {
        val body = json.encodeToString(ToolResultRequest.serializer(), ToolResultRequest(toolCallId, result))
        return send(post(url("api/chat/$conversationId/tool-result"), body.toRequestBody(JSON)), allow404 = true)
            .use { it.code != 404 }
    }

    /**
     * The reply sentence as MP3 from the user's speech model (ADR 0038), or null when the user has none (409): the
     * caller falls back to the platform voice, as the web client falls back to speechSynthesis.
     */
    suspend fun speech(text: String): ByteArray? {
        val body = json.encodeToString(SpeechRequest.serializer(), SpeechRequest(text))
        return send(post(url("api/speech"), body.toRequestBody(JSON)), allow409 = true).use {
            if (it.code == 409) null else it.body.bytes()
        }
    }

    suspend fun upload(conversationId: String, bytes: ByteArray, fileName: String, mediaType: String): UploadResult {
        require(bytes.size <= MAX_ATTACHMENT_BYTES) { "Attachments are limited to 10 MB." }
        val body = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", fileName, bytes.toRequestBody(mediaType.toMediaType()))
            .build()
        val uploaded = send(post(url("api/conversations/$conversationId/attachments"), body)).use {
            json.decodeFromString(UploadResponse.serializer(), it.body.string())
        }
        return if (uploaded.id != null) {
            UploadResult.Image(uploaded.id, uploaded.mediaType ?: mediaType, uploaded.fileName ?: fileName)
        } else {
            UploadResult.Document(uploaded.fileName ?: fileName, uploaded.chunkCount ?: 0)
        }
    }

    /**
     * Sends a turn and streams its events. Collecting stops when the stream ends; cancelling the collector cancels
     * the HTTP call, but NOT the turn: the server keeps going (ADR 0046), and only [stop] ends it.
     */
    fun chat(conversationId: String, request: ChatRequest): Flow<StreamEvent> = flow {
        val body = json.encodeToString(ChatRequest.serializer(), request).toRequestBody(JSON)
        val call = streaming.newCall(post(url("api/chat/$conversationId"), body))
        currentCoroutineContext().job.invokeOnCompletion { call.cancel() }
        call.execute().use { response ->
            if (!response.isSuccessful) throw ApiException(response.code, "Chat failed (HTTP ${response.code})")
            readNdjson(response.body.source()) { emit(it) }
        }
    }.flowOn(Dispatchers.IO)

    private fun url(path: String): HttpUrl = base.newBuilder().addPathSegments(path).build()

    private fun post(url: HttpUrl, body: RequestBody): Request =
        Request.Builder().url(url).post(body).also(credentials::applyTo).build()

    private suspend fun <T> get(path: String, serializer: KSerializer<T>): T = get(url(path), serializer)

    private suspend fun <T> get(url: HttpUrl, serializer: KSerializer<T>): T {
        val request = Request.Builder().url(url).get().also(credentials::applyTo).build()
        return send(request).use { json.decodeFromString(serializer, it.body.string()) }
    }

    private suspend fun send(request: Request, allow404: Boolean = false, allow409: Boolean = false): Response =
        withContext(Dispatchers.IO) {
            val response = http.newCall(request).execute()
            val allowed = (allow404 && response.code == 404) || (allow409 && response.code == 409)
            if (!response.isSuccessful && !allowed) {
                response.close()
                throw ApiException(response.code, "${request.method} ${request.url.encodedPath} failed (HTTP ${response.code})")
            }
            response
        }

    companion object {
        const val MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024
        private val JSON = "application/json".toMediaType()
        private val EMPTY_BODY = ByteArray(0).toRequestBody(null)
        internal val json = Json {
            ignoreUnknownKeys = true
            explicitNulls = false
        }

        /**
         * Reads NDJSON lines as they arrive. A line split across network reads is reassembled by the buffered
         * source; a blank line is skipped.
         */
        internal suspend fun readNdjson(source: BufferedSource, onEvent: suspend (StreamEvent) -> Unit) {
            while (true) {
                val line = source.readUtf8Line() ?: break
                StreamEventParser.parse(line)?.let { onEvent(it) }
            }
        }
    }
}
