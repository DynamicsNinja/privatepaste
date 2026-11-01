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
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.core.model.HandoverCreated
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.ui.settings.AddDeviceState
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

    @Test
    fun changePassphraseOwnerOnly() = runTest(UnconfinedTestDispatcher()) {
        val owner = MutableStateFlow(false)
        val changes = mutableListOf<List<String>>()
        var answer: UiText? = UiText.Res(R.string.vault_wrong_passphrase)
        val vm = collecting(
            SettingsViewModel(
                session = MutableStateFlow(StoredSession("https://paste.example.com/", "dev-1", "Pixel 9", "token")),
                readDeliveryMode = { mode }, writeDeliveryMode = { mode = it }, isBatteryExempt = { true },
                performSignOut = {}, appVersion = "0.1.0",
                isOwner = owner,
                performChangePassphrase = { current, next, repeat ->
                    changes += listOf(current, next, repeat)
                    answer
                },
            ),
        )

        assertFalse(vm.state.value.isOwner)
        vm.changePassphrase("old passphrase 1", "new passphrase 22", "new passphrase 22")
        assertEquals("not the owner: nothing is sent", emptyList<List<String>>(), changes)

        owner.value = true
        assertTrue(vm.state.value.isOwner)
        vm.changePassphrase("old passphrase 1", "new passphrase 22", "new passphrase 22")
        assertEquals(listOf(listOf("old passphrase 1", "new passphrase 22", "new passphrase 22")), changes)
        assertEquals(UiText.Res(R.string.vault_wrong_passphrase), vm.state.value.passphraseError)
        assertFalse(vm.state.value.passphraseChanged)

        answer = null
        vm.changePassphrase("old passphrase 1", "new passphrase 22", "new passphrase 22")
        assertTrue("Passphrase changed.", vm.state.value.passphraseChanged)
        assertNull(vm.state.value.passphraseError)
    }

    @Test
    fun addDeviceShowsTheCodeOrTheFailure() = runTest(UnconfinedTestDispatcher()) {
        val expires = java.time.Instant.parse("2026-11-01T10:10:00Z")
        var fail = false
        val vm = collecting(
            SettingsViewModel(
                session = MutableStateFlow(null), readDeliveryMode = { mode }, writeDeliveryMode = {},
                isBatteryExempt = { true }, performSignOut = {}, appVersion = "0.1.0",
                createHandover = {
                    if (fail) throw java.io.IOException("offline")
                    HandoverCreated("2f4f7a8e-6b7a-4f3c-9e55-2d2b1c0e9a11", expires) to "privatepaste:handover?id=x"
                },
            ),
        )
        vm.addDevice()
        assertEquals(AddDeviceState(loading = false, qrText = "privatepaste:handover?id=x", expiresAt = expires), vm.state.value.addDevice)

        fail = true
        vm.addDevice()
        assertEquals(UiText.Res(R.string.add_device_failed), vm.state.value.addDevice?.error)
        vm.closeAddDevice()
        assertNull(vm.state.value.addDevice)
    }
}
