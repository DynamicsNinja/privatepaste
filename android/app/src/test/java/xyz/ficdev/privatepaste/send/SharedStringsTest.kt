package xyz.ficdev.privatepaste.send

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.R

/** Messages shown both by code without a Context (ClipSender) and by activities come from one source. */
@RunWith(RobolectricTestRunner::class)
class SharedStringsTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()

    @Test
    fun senderConstantMatchesTheResource() {
        assertEquals(context.getString(R.string.sign_in_first), ClipSender.SIGN_IN_FIRST)
        assertEquals(ClipSender.SIGN_IN_FIRST, SendResult.SignedOut.message)
        assertEquals("Sign in to PrivatePaste first", context.getString(R.string.sign_in_first))
    }

    @Test
    fun copyMessagesAreUnchanged() {
        assertEquals("Copied", context.getString(R.string.copied))
        assertEquals("Couldn't copy the clip.", context.getString(R.string.copy_failed))
    }
}
