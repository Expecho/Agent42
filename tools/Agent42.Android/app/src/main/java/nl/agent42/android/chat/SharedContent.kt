package nl.agent42.android.chat

/** What another app shared into Agent 42 (the share sheet): text for the composer and files (content URIs) to attach. */
data class SharedContent(val text: String?, val files: List<String>) {
    companion object {
        /**
         * The composer prefill, as the PWA share target builds it (sharetarget.js): the shared text (on Android it
         * already carries the URL), else the subject/title.
         */
        fun text(subject: String?, text: String?): String? =
            text?.trim()?.takeIf { it.isNotEmpty() } ?: subject?.trim()?.takeIf { it.isNotEmpty() }
    }
}
