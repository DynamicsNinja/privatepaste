package xyz.ficdev.privatepaste.ui

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import xyz.ficdev.privatepaste.sync.SignInResult
import xyz.ficdev.privatepaste.ui.signin.SignInViewModel

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
class SignInViewModelTest {

    private val dispatcher = StandardTestDispatcher()
    private val context = ApplicationProvider.getApplicationContext<Context>()

    private val calls = mutableListOf<List<String>>()
    private var servicesStarted = 0

    @Before fun setUp() = Dispatchers.setMain(dispatcher)
    @After fun tearDown() = Dispatchers.resetMain()

    private fun vm(initialUrl: String? = null, initialUsername: String? = null, result: suspend () -> SignInResult) = SignInViewModel(
        initialUrl = initialUrl,
        initialUsername = initialUsername,
        defaultDeviceName = "Pixel 9",
        signIn = { url, username, password, name -> calls += listOf(url, username, password, name); result() },
        onSignedIn = { servicesStarted++ },
    )

    private fun SignInViewModel.fill() {
        onUrlChange(" https://paste.example.com ")
        onUsernameChange(" Ana ")
        onPasswordChange("dev")
    }

    private fun errorFor(result: SignInResult): String? {
        var error: String? = null
        runTest(dispatcher) {
            val vm = vm { result }
            vm.fill()
            vm.submit()
            advanceUntilIdle()
            assertFalse(vm.state.value.busy)
            error = vm.state.value.error?.resolve(context)
        }
        assertEquals(0, servicesStarted)
        return error
    }

    @Test
    fun wrongPassword() = assertEquals("Wrong name or password.", errorFor(SignInResult.WrongPassword))

    @Test
    fun rateLimited() =
        assertEquals("Too many attempts. Wait a minute and try again.", errorFor(SignInResult.RateLimited))

    @Test
    fun invalidUrlShowsTheServerUrlMessage() =
        assertEquals("Use https:// for the server address.", errorFor(SignInResult.InvalidUrl("Use https:// for the server address.")))

    @Test
    fun unreachable() = assertEquals(
        "Can't reach the server. Check the address and your connection.",
        errorFor(SignInResult.Unreachable("java.net.UnknownHostException: paste.example.com")),
    )

    @Test
    fun failedShowsItsMessage() = assertEquals("Server error (500).", errorFor(SignInResult.Failed("Server error (500).")))

    @Test
    fun okClearsErrorAndPasswordAndStartsTheService() = runTest(dispatcher) {
        var next: SignInResult = SignInResult.WrongPassword
        val vm = vm { next }
        vm.fill()
        vm.submit()
        advanceUntilIdle()
        assertEquals(0, servicesStarted)

        next = SignInResult.Ok
        vm.submit()
        advanceUntilIdle()
        assertNull(vm.state.value.error)
        assertEquals("", vm.state.value.password)
        assertEquals(1, servicesStarted)
        assertEquals(listOf("https://paste.example.com", "Ana", "dev", "Pixel 9"), calls.last())
    }

    @Test
    fun buttonIsDisabledWhileBusy() = runTest(dispatcher) {
        val gate = CompletableDeferred<SignInResult>()
        val vm = vm { gate.await() }
        vm.fill()
        assertTrue(vm.state.value.canSubmit)

        vm.submit()
        advanceUntilIdle()
        assertTrue(vm.state.value.busy)
        assertFalse(vm.state.value.canSubmit)

        vm.submit()   // ignored while busy
        advanceUntilIdle()
        assertEquals(1, calls.size)

        gate.complete(SignInResult.WrongPassword)
        advanceUntilIdle()
        assertFalse(vm.state.value.busy)
        assertTrue(vm.state.value.canSubmit)
    }

    @Test
    fun buttonIsDisabledUntilTheFormIsValid() {
        val vm = vm { SignInResult.Ok }
        assertFalse(vm.state.value.canSubmit)
        vm.onUrlChange("https://paste.example.com")
        assertFalse(vm.state.value.canSubmit)
        vm.onPasswordChange("dev")
        assertFalse(vm.state.value.canSubmit)   // the name is required
        vm.onUsernameChange("Ana")
        assertTrue(vm.state.value.canSubmit)
        vm.onDeviceNameChange("   ")
        assertFalse(vm.state.value.canSubmit)
        vm.onDeviceNameChange("x".repeat(51))
        assertFalse(vm.state.value.canSubmit)
        vm.onDeviceNameChange("x".repeat(50))
        assertTrue(vm.state.value.canSubmit)
    }

    @Test
    fun signIn_requiresName() {
        val vm = vm { SignInResult.Ok }
        vm.onUrlChange("https://paste.example.com")
        vm.onPasswordChange("dev")
        vm.onUsernameChange("   ")
        assertFalse(vm.state.value.canSubmit)
        vm.submit()
        assertEquals(0, calls.size)
    }

    @Test
    fun signIn_prefillsLastName() {
        val vm = vm(initialUsername = "Ana") { SignInResult.Ok }
        assertEquals("Ana", vm.state.value.username)
    }

    @Test
    fun prefillsTheLastServerAndDeviceName() {
        val vm = vm(initialUrl = "https://paste.example.com") { SignInResult.Ok }
        assertEquals("https://paste.example.com", vm.state.value.url)
        assertEquals("Pixel 9", vm.state.value.deviceName)
    }
}
