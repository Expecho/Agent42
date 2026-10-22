package nl.agent42.android.chat

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SharedContentTest {

    @Test
    fun theSharedTextWins_elseTheSubject() {
        assertEquals("Look https://x.nl", SharedContent.text("Page title", " Look https://x.nl "))
        assertEquals("Page title", SharedContent.text("Page title", "  "))
        assertNull(SharedContent.text(null, null))
    }
}
