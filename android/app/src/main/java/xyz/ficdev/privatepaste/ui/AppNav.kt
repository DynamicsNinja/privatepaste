package xyz.ficdev.privatepaste.ui

import android.os.Build
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import kotlinx.coroutines.flow.MutableStateFlow
import xyz.ficdev.privatepaste.storage.StoredSession
import xyz.ficdev.privatepaste.ui.history.HistoryScreen
import xyz.ficdev.privatepaste.ui.history.HistoryViewModel
import xyz.ficdev.privatepaste.ui.history.ThumbnailLoader
import xyz.ficdev.privatepaste.ui.history.asClipCopier
import xyz.ficdev.privatepaste.ui.history.asClipRemote
import xyz.ficdev.privatepaste.ui.onboarding.PermissionsStep
import xyz.ficdev.privatepaste.ui.settings.SettingsScreen
import xyz.ficdev.privatepaste.ui.settings.SettingsViewModel
import xyz.ficdev.privatepaste.ui.signin.SignInScreen
import xyz.ficdev.privatepaste.ui.signin.SignInViewModel

/**
 * State-driven navigation: signed out shows sign-in; signed in before onboarding shows the permissions step;
 * otherwise history, with settings on top of it.
 */
@Composable
fun AppNav(deps: UiDeps, relativeTime: RelativeTime = remember { RelativeTime() }) {
    val session by deps.session.collectAsStateWithLifecycle()
    var onboardingDone by remember { mutableStateOf(deps.settings.onboardingDone) }
    var showSettings by rememberSaveable { mutableStateOf(false) }

    val current = session
    LaunchedEffect(current == null) {
        if (current == null) showSettings = false   // the next sign-in starts on the history
    }
    when {
        current == null -> SignInRoute(deps)
        !onboardingDone -> PermissionsStep(
            isBatteryExempt = deps.isBatteryExempt,
            requestBatteryExemption = deps.requestBatteryExemption,
            onDone = {
                deps.settings.onboardingDone = true
                onboardingDone = true
                deps.startService()
            },
        )
        showSettings -> SettingsRoute(deps, current, onBack = { showSettings = false })
        else -> HistoryRoute(deps, current, relativeTime, onOpenSettings = { showSettings = true })
    }
}

@Composable
private fun SignInRoute(deps: UiDeps) {
    val vm: SignInViewModel = viewModel(
        key = "signin",
        factory = viewModelFactory {
            initializer {
                SignInViewModel(
                    initialUrl = deps.lastServerUrl,
                    defaultDeviceName = deps.lastDeviceName ?: Build.MODEL.orEmpty(),
                    signIn = deps::signIn,
                    onSignedIn = deps.startService,
                )
            }
        },
    )
    SignInScreen(vm)
}

/** Keyed by session, so a new sign-in gets a fresh list (no rows hidden by an earlier session's deletes). */
private fun StoredSession.key() = "$deviceId/${token.hashCode()}"

@Composable
private fun HistoryRoute(deps: UiDeps, session: StoredSession, relativeTime: RelativeTime, onOpenSettings: () -> Unit) {
    val vm: HistoryViewModel = viewModel(
        key = "history/${session.key()}",
        factory = viewModelFactory {
            initializer {
                val cache = deps.cache
                HistoryViewModel(
                    cached = cache?.items ?: MutableStateFlow(emptyList()),
                    lastAppliedHash = { cache?.lastAppliedHash },
                    syncState = deps.syncState,
                    remote = { deps.api()?.asClipRemote() },
                    copier = deps.clipboard.asClipCopier(),
                    cachedImage = deps.cachedImage,
                    onUnauthorized = deps::onUnauthorized,
                )
            }
        },
    )
    val thumbnails = remember(session.key()) {
        ThumbnailLoader(deps.thumbs, remote = { deps.api()?.asClipRemote() }, onUnauthorized = deps::onUnauthorized)
    }
    HistoryScreen(vm, thumbnails, relativeTime, onOpenSettings)
}

@Composable
private fun SettingsRoute(deps: UiDeps, session: StoredSession, onBack: () -> Unit) {
    val vm: SettingsViewModel = viewModel(
        key = "settings/${session.key()}",
        factory = viewModelFactory {
            initializer {
                SettingsViewModel(
                    session = deps.session,
                    readDeliveryMode = { deps.settings.deliveryMode },
                    writeDeliveryMode = { deps.settings.deliveryMode = it },
                    isBatteryExempt = deps.isBatteryExempt,
                    performSignOut = deps::signOut,
                    appVersion = deps.appVersion,
                )
            }
        },
    )
    SettingsScreen(vm, deps.requestBatteryExemption, onBack)
}
