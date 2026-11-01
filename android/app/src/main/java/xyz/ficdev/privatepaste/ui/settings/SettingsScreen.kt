package xyz.ficdev.privatepaste.ui.settings

import android.content.ActivityNotFoundException
import android.content.Intent
import android.provider.Settings
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.storage.DeliveryMode
import xyz.ficdev.privatepaste.ui.asString
import xyz.ficdev.privatepaste.ui.unlock.PassphraseField

private val PanelShape = RoundedCornerShape(14.dp)

@Composable
fun SettingsScreen(
    vm: SettingsViewModel,
    requestBatteryExemption: () -> Intent,
    onBack: () -> Unit,
) {
    val state by vm.state.collectAsStateWithLifecycle()
    val batteryLauncher = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { vm.refresh() }
    LifecycleResumeEffect(vm) {
        vm.refresh()
        onPauseOrDispose {}
    }
    val context = LocalContext.current
    state.addDevice?.let { addDevice ->
        AddDeviceScreen(addDevice, onNewCode = vm::addDevice, onExpired = vm::expireAddDevice, onBack = vm::closeAddDevice)
        return
    }
    BackHandler(onBack = onBack)
    SettingsContent(
        state = state,
        onDeliveryMode = vm::setDeliveryMode,
        onAllowBackground = {
            try {
                batteryLauncher.launch(requestBatteryExemption())
            } catch (_: ActivityNotFoundException) {
                // Some OEM builds have no such screen; the status stays "Restricted".
            }
        },
        onOpenNotificationSettings = {
            val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
            try {
                context.startActivity(intent)   // the state is re-read on resume
            } catch (_: ActivityNotFoundException) {
                // No such screen on this build: the line stays.
            }
        },
        onSignOut = vm::signOut,
        onBack = onBack,
        onChangePassphrase = vm::changePassphrase,
        onClearPassphraseResult = vm::clearPassphraseResult,
        onAddDevice = vm::addDevice,
    )
}

@Composable
fun SettingsContent(
    state: SettingsState,
    onDeliveryMode: (DeliveryMode) -> Unit,
    onAllowBackground: () -> Unit,
    onSignOut: () -> Unit,
    onBack: () -> Unit,
    onOpenNotificationSettings: () -> Unit = {},
    onChangePassphrase: (current: String, next: String, repeat: String) -> Unit = { _, _, _ -> },
    onClearPassphraseResult: () -> Unit = {},
    onAddDevice: () -> Unit = {},
) {
    var confirmSignOut by rememberSaveable { mutableStateOf(false) }
    var changing by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(state.passphraseChanged) {
        if (state.passphraseChanged) changing = false
    }
    val colors = MaterialTheme.colorScheme

    Surface(modifier = Modifier.fillMaxSize(), color = colors.background) {
        Column(modifier = Modifier.safeDrawingPadding()) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(start = 4.dp, end = 16.dp, top = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(onClick = onBack) {
                    Icon(
                        painter = painterResource(R.drawable.ic_back),
                        contentDescription = stringResource(R.string.settings_back),
                        tint = colors.onBackground,
                    )
                }
                Text(stringResource(R.string.settings_title), style = MaterialTheme.typography.titleLarge, color = colors.onBackground)
            }

            Column(
                modifier = Modifier
                    .verticalScroll(rememberScrollState())
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Panel {
                    Field(stringResource(R.string.settings_server), state.serverUrl)
                    Field(stringResource(R.string.settings_device_name), state.deviceName)
                }

                Panel(title = stringResource(R.string.settings_delivery)) {
                    Column(Modifier.selectableGroup()) {
                        DeliveryOption(
                            label = stringResource(R.string.settings_delivery_automatic),
                            selected = state.deliveryMode == DeliveryMode.AUTOMATIC,
                            onSelect = { onDeliveryMode(DeliveryMode.AUTOMATIC) },
                        )
                        DeliveryOption(
                            label = stringResource(R.string.settings_delivery_notification),
                            selected = state.deliveryMode == DeliveryMode.NOTIFICATION,
                            onSelect = { onDeliveryMode(DeliveryMode.NOTIFICATION) },
                        )
                    }
                    if (state.notificationsOff) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                text = stringResource(R.string.settings_notifications_off),
                                style = MaterialTheme.typography.bodyMedium,
                                color = colors.error,
                                modifier = Modifier.weight(1f),
                            )
                            TextButton(onClick = onOpenNotificationSettings, modifier = Modifier.heightIn(min = 48.dp)) {
                                Text(stringResource(R.string.settings_open_settings))
                            }
                        }
                    }
                }

                Panel(title = stringResource(R.string.settings_background)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = stringResource(
                                if (state.batteryExempt) R.string.settings_background_allowed else R.string.settings_background_restricted,
                            ),
                            style = MaterialTheme.typography.bodyLarge,
                            color = colors.onSurface,
                            modifier = Modifier.weight(1f),
                        )
                        if (!state.batteryExempt) {
                            Button(onClick = onAllowBackground, shape = RoundedCornerShape(10.dp), modifier = Modifier.heightIn(min = 48.dp)) {
                                Text(stringResource(R.string.settings_allow))
                            }
                        }
                    }
                }

                Panel(title = stringResource(R.string.settings_encryption)) {
                    if (state.isOwner) {
                        ActionRow(stringResource(R.string.settings_change_passphrase)) {
                            onClearPassphraseResult()
                            changing = true
                        }
                        if (state.passphraseChanged) {
                            Text(
                                text = stringResource(R.string.settings_passphrase_changed),
                                style = MaterialTheme.typography.bodyMedium,
                                color = colors.primary,
                                modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                            )
                        }
                    }
                    ActionRow(stringResource(R.string.settings_add_device), onAddDevice)
                }

                OutlinedButton(
                    onClick = { confirmSignOut = true },
                    enabled = !state.signingOut,
                    shape = RoundedCornerShape(10.dp),
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 48.dp),
                ) {
                    Text(stringResource(R.string.settings_sign_out))
                }

                Text(
                    text = stringResource(R.string.settings_version, state.appVersion),
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.onSurfaceVariant,
                    modifier = Modifier.align(Alignment.CenterHorizontally),
                )
            }
        }
    }

    if (confirmSignOut) {
        AlertDialog(
            onDismissRequest = { confirmSignOut = false },
            text = { Text(stringResource(R.string.settings_sign_out_confirm), style = MaterialTheme.typography.bodyLarge) },
            confirmButton = {
                TextButton(onClick = {
                    confirmSignOut = false
                    onSignOut()
                }) {
                    Text(stringResource(R.string.settings_sign_out), color = colors.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmSignOut = false }) { Text(stringResource(R.string.settings_cancel)) }
            },
        )
    }

    if (changing && state.isOwner) {
        ChangePassphraseDialog(state, onChange = onChangePassphrase, onDismiss = { changing = false })
    }
}

/** Current, new and repeat. Kept in `remember`, not saved state: passphrases never go into the saved instance bundle. */
@Composable
private fun ChangePassphraseDialog(
    state: SettingsState,
    onChange: (current: String, next: String, repeat: String) -> Unit,
    onDismiss: () -> Unit,
) {
    var current by remember { mutableStateOf("") }
    var next by remember { mutableStateOf("") }
    var repeat by remember { mutableStateOf("") }
    val busy = state.changingPassphrase
    val canChange = !busy && current.isNotEmpty() && next.isNotEmpty() && repeat.isNotEmpty()
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.settings_change_passphrase)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                PassphraseField(current, { current = it }, R.string.settings_current_passphrase, enabled = !busy, imeAction = ImeAction.Next)
                PassphraseField(next, { next = it }, R.string.settings_new_passphrase, enabled = !busy, imeAction = ImeAction.Next)
                PassphraseField(
                    value = repeat,
                    onValueChange = { repeat = it },
                    label = R.string.vault_repeat_passphrase,
                    enabled = !busy,
                    imeAction = ImeAction.Go,
                    onGo = { if (canChange) onChange(current, next, repeat) },
                )
                Text(
                    text = stringResource(R.string.vault_rule),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                state.passphraseError?.let {
                    Text(
                        text = it.asString(),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                    )
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { onChange(current, next, repeat) }, enabled = canChange) {
                if (busy) {
                    CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.size(8.dp))
                }
                Text(stringResource(R.string.settings_change))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.settings_cancel)) }
        },
    )
}

@Composable
private fun ActionRow(label: String, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .clip(RoundedCornerShape(10.dp))
            .clickable(onClick = onClick, role = Role.Button),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.primary)
    }
}

@Composable
private fun Panel(title: String? = null, content: @Composable ColumnScope.() -> Unit) {
    val colors = MaterialTheme.colorScheme
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (title != null) {
            Text(title, style = MaterialTheme.typography.titleSmall, color = colors.onSurfaceVariant, modifier = Modifier.padding(start = 4.dp))
        }
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .clip(PanelShape)
                .background(colors.surface)
                .border(1.dp, colors.outline, PanelShape)
                .padding(horizontal = 14.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            content = content,
        )
    }
}

@Composable
private fun Field(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(label, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurface)
    }
}

@Composable
private fun DeliveryOption(label: String, selected: Boolean, onSelect: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp)
            .clip(RoundedCornerShape(10.dp))
            .selectable(selected = selected, onClick = onSelect, role = Role.RadioButton),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = null)
        Spacer(Modifier.padding(start = 10.dp))
        Text(label, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurface)
    }
}
