package nl.agent42.android.voice

/** Text rules of the voice loop, ported from the web client's voice.js so both clients speak and listen alike. */
object SpeechText {

    /** What is read aloud: markdown syntax removed (code blocks dropped, links reduced to their text). voice.js stripMarkdown. */
    fun stripMarkdown(text: String): String = text
        .replace(Regex("```[\\s\\S]*?```"), " ")
        .replace(Regex("`([^`]+)`"), "$1")
        .replace(Regex("!\\[[^\\]]*]\\([^)]*\\)"), " ")
        .replace(Regex("\\[([^\\]]+)]\\([^)]*\\)"), "$1")
        .replace(Regex("[*_#>~]+"), " ")
        .replace(Regex("\\s+"), " ")
        .trim()

    /**
     * A recognition result heard while the assistant talks is an echo of its own reply (or the trailing final of what
     * was just sent) when at least 60% of its words already appear there. voice.js isSelfOrEcho. On the call audio
     * path the phone's echo canceller makes this rare (ADR 0114), so it is a backstop, not the mechanism.
     */
    fun isSelfOrEcho(heard: String, reference: String): Boolean {
        val words = WORD.findAll(heard.lowercase()).map { it.value }.toList()
        if (words.isEmpty()) return true
        val ref = reference.lowercase()
        return words.count { ref.contains(it) }.toDouble() / words.size >= ECHO_OVERLAP
    }

    fun wordCount(text: String): Int = WORD.findAll(text).count()

    private val WORD = Regex("\\p{L}+")
    private const val ECHO_OVERLAP = 0.6
}

/** Accumulates reply deltas and hands out whole sentences, so speech starts before the reply is complete. voice.js flush. */
class SentenceBuffer {
    private val buffer = StringBuilder()

    /** Adds a delta; returns everything up to the LAST sentence terminator, or null when no sentence is complete yet. */
    fun push(delta: String): String? {
        buffer.append(delta)
        val cut = buffer.indexOfLast { it == '.' || it == '!' || it == '?' || it == '\n' }
        if (cut < 0) return null
        val chunk = buffer.substring(0, cut + 1)
        buffer.delete(0, cut + 1)
        return chunk
    }

    /** The tail at stream end, or null when nothing is left. */
    fun flush(): String? = buffer.toString().also { buffer.clear() }.takeIf { it.isNotBlank() }

    fun clear() {
        buffer.clear()
    }
}
