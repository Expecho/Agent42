package nl.agent42.android.chat

import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/** A position for `get_current_location`. */
data class Fix(val latitude: Double, val longitude: Double, val accuracyMetres: Double?, val capturedAt: Instant)

/**
 * The client tools this app runs (ADR 0089/0093/0114) and the exact result shapes the web client posts back
 * (wwwroot/js/clienttools.js, location.js), so the model sees the same answer whichever client it ran on.
 */
object ClientTools {
    const val LOCATION = "get_current_location"
    const val CONFIRM = "confirm_action"

    /**
     * What a chat request advertises. Only the chat screen in the foreground can show a dialog or take a fix under a
     * while-in-use grant; voice mode (screen off) advertises nothing, so the server answers "unavailable" at once
     * instead of making the model wait out a timeout.
     */
    fun advertised(chatInForeground: Boolean, voice: Boolean): List<String> =
        if (chatInForeground && !voice) listOf(LOCATION, CONFIRM) else emptyList()

    fun location(fix: Fix): JsonObject = buildJsonObject {
        put("latitude", fix.latitude)
        put("longitude", fix.longitude)
        put("accuracyMetres", fix.accuracyMetres)
        put("capturedAt", fix.capturedAt.toString())
    }

    /** The user hasn't granted location (or just declined). */
    val NOT_SHARED: JsonObject = status("not_shared")

    /** Granted, but no fix could be taken within the bound. */
    val NO_FIX: JsonObject = status("no_fix")

    /** A tool this client doesn't run (never advertised, but a stale pending call can still name one). */
    val UNAVAILABLE: JsonObject = status("unavailable")

    fun confirmed(approved: Boolean): JsonObject = buildJsonObject { put("approved", approved) }

    /** The confirm dialog's text: the model's `action`, or a neutral fallback. Shown as plain text, never markup. */
    fun confirmPrompt(toolInput: String?): String =
        runCatching { Json.parseToJsonElement(toolInput ?: "{}").jsonObject["action"]?.jsonPrimitive?.content }
            .getOrNull()
            ?.takeIf { it.isNotBlank() }
            ?: "The assistant wants to take an action."

    private fun status(value: String): JsonObject = JsonObject(mapOf("status" to JsonPrimitive(value)))
}
