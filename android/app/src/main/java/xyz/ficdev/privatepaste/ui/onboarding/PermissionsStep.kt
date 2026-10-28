package xyz.ficdev.privatepaste.ui.onboarding

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import xyz.ficdev.privatepaste.R

private enum class Step { NOTIFICATIONS, BATTERY, DONE }

/**
 * One-time onboarding after the first sign-in: on Android 13+ the notification permission (for the quiet
 * "connected" notification), then the battery-optimization exemption ("Allow" or "Not now"). [onDone] runs once
 * both steps are answered or not needed; the caller records it and starts the service.
 */
@Composable
fun PermissionsStep(
    isBatteryExempt: () -> Boolean,
    requestBatteryExemption: () -> Intent,
    onDone: () -> Unit,
) {
    val context = LocalContext.current
    var step by rememberSaveable { mutableStateOf(firstStep(context, isBatteryExempt)) }
    val afterNotifications = { step = if (isBatteryExempt()) Step.DONE else Step.BATTERY }

    val notificationLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        afterNotifications()
    }
    val batteryLauncher = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        step = Step.DONE
    }

    LaunchedEffect(step) {
        if (step == Step.DONE) onDone()
    }

    when (step) {
        Step.NOTIFICATIONS -> StepLayout(
            title = stringResource(R.string.onboarding_notifications_title),
            body = stringResource(R.string.onboarding_notifications_body),
            primary = stringResource(R.string.onboarding_continue),
            onPrimary = {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    notificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                } else {
                    afterNotifications()
                }
            },
        )
        Step.BATTERY -> StepLayout(
            title = stringResource(R.string.onboarding_battery_title),
            body = stringResource(R.string.onboarding_battery_body),
            primary = stringResource(R.string.onboarding_allow),
            onPrimary = {
                try {
                    batteryLauncher.launch(requestBatteryExemption())
                } catch (_: ActivityNotFoundException) {
                    step = Step.DONE
                }
            },
            secondary = stringResource(R.string.onboarding_not_now),
            onSecondary = { step = Step.DONE },
        )
        Step.DONE -> Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {}
    }
}

private fun firstStep(context: Context, isBatteryExempt: () -> Boolean): Step = when {
    Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED ->
        Step.NOTIFICATIONS
    !isBatteryExempt() -> Step.BATTERY
    else -> Step.DONE
}

@Composable
private fun StepLayout(
    title: String,
    body: String,
    primary: String,
    onPrimary: () -> Unit,
    secondary: String? = null,
    onSecondary: () -> Unit = {},
) {
    val colors = MaterialTheme.colorScheme
    Surface(modifier = Modifier.fillMaxSize(), color = colors.background) {
        Box(
            modifier = Modifier
                .safeDrawingPadding()
                .verticalScroll(rememberScrollState()),
            contentAlignment = Alignment.TopCenter,
        ) {
            Column(
                modifier = Modifier
                    .widthIn(max = 420.dp)
                    .fillMaxWidth()
                    .padding(horizontal = 24.dp, vertical = 48.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Image(painterResource(R.drawable.ic_logo), contentDescription = null, modifier = Modifier.size(64.dp))
                Text(title, style = MaterialTheme.typography.headlineSmall, color = colors.onBackground, textAlign = TextAlign.Center)
                Text(body, style = MaterialTheme.typography.bodyLarge, color = colors.onSurfaceVariant, textAlign = TextAlign.Center)
                Button(
                    onClick = onPrimary,
                    shape = RoundedCornerShape(10.dp),
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 52.dp),
                ) { Text(primary) }
                if (secondary != null) {
                    TextButton(onClick = onSecondary, modifier = Modifier.heightIn(min = 48.dp)) { Text(secondary) }
                }
            }
        }
    }
}
