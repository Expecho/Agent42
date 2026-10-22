package nl.agent42.android.session

import nl.agent42.android.api.Credentials
import nl.agent42.android.api.SessionCookie
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionTest {

    @Test
    fun anHttpsServerNeedsSignIn_untilItHasACookie() {
        assertTrue(Session("https://chat.agent42.nl").needsSignIn)
        assertFalse(Session("https://chat.agent42.nl", cookie = "abc").needsSignIn)
        assertTrue(Session("https://chat.agent42.nl", cookie = "abc").credentials is SessionCookie)
    }

    @Test
    fun aPlainHttpDevHostNeverSignsIn_andSendsNoCookie() {
        val devHost = Session("http://localhost:61326", cookie = "stale")

        assertFalse(devHost.needsSignIn)
        assertSame(Credentials.None, devHost.credentials)
    }

    @Test
    fun urlsAreNormalized() {
        assertEquals("https://chat.agent42.nl", Session.normalizeUrl(" chat.agent42.nl/ "))
        assertEquals("http://localhost:61326", Session.normalizeUrl("http://localhost:61326"))
        assertNull(Session.normalizeUrl(""))
        assertNull(Session.normalizeUrl("ftp://x"))
        assertNull(Session.normalizeUrl("not a url"))
        assertNull(Session.normalizeUrl("https://"))
    }
}
