package xyz.ficdev.privatepaste.storage

import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class SettingsStoreTest {

    private val context = ApplicationProvider.getApplicationContext<android.content.Context>()

    @Test
    fun defaultsAndPersistence() {
        val settings = SettingsStore(context)
        assertEquals(DeliveryMode.AUTOMATIC, settings.deliveryMode)
        assertFalse(settings.onboardingDone)

        settings.deliveryMode = DeliveryMode.NOTIFICATION
        settings.onboardingDone = true

        val again = SettingsStore(context)
        assertEquals(DeliveryMode.NOTIFICATION, again.deliveryMode)
        assertTrue(again.onboardingDone)
    }

    @Test
    fun unknownDeliveryModeFallsBackToAutomatic() {
        context.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE)
            .edit().putString("deliveryMode", "SOMETHING_ELSE").commit()
        assertEquals(DeliveryMode.AUTOMATIC, SettingsStore(context).deliveryMode)
    }
}
