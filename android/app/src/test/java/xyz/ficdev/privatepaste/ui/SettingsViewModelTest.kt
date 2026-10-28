package xyz.ficdev.privatepaste.ui

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.ui.settings.SettingsViewModel

@OptIn(ExperimentalCoroutinesApi::class)
class SettingsViewModelTest {

    private var mode = DeliveryMode.AUTOMATIC
    private var notificationsEnabled = true

    @Before fun setUp() = Dispatchers.setMain(UnconfinedTestDispatcher())
    @After fun tearDown() = Dispatchers.resetMain()

    private fun vm() = SettingsViewModel(
        session = MutableStateFlow(StoredSession("https://paste.example.com/", "dev-1", "Pixel 9", "token")),
        readDeliveryMode = { mode },
        writeDeliveryMode = { mode = it },
        isBatteryExempt = { true },
        performSignOut = {},
        appVersion = "0.1.0",
        areNotificationsEnabled = { notificationsEnabled },
    )

    /** Keeps the state collected, like the screen does. */
    private fun TestScope.collecting(vm: SettingsViewModel): SettingsViewModel {
        backgroundScope.launch { vm.state.collect {} }
        return vm
    }

    @Test
    fun warnsOnlyForNotificationDeliveryWithNotificationsOff() = runTest(UnconfinedTestDispatcher()) {
        notificationsEnabled = false
        val vm = collecting(vm())
        assertFalse("automatic copy needs no notifications", vm.state.value.notificationsOff)

        vm.setDeliveryMode(DeliveryMode.NOTIFICATION)
        assertTrue(vm.state.value.notificationsOff)

        notificationsEnabled = true   // the user turned them on in the system settings
        vm.refresh()                  // on resume
        assertFalse(vm.state.value.notificationsOff)

        notificationsEnabled = false
        vm.refresh()
        assertTrue(vm.state.value.notificationsOff)
    }

    @Test
    fun startsWithTheWarningWhenAlreadyInNotificationMode() = runTest {
        mode = DeliveryMode.NOTIFICATION
        notificationsEnabled = false
        assertTrue(vm().state.value.notificationsOff)
    }
}
