package xyz.ficdev.privatepaste.ui.unlock

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.sync.VaultState
import xyz.ficdev.privatepaste.ui.BrandMark
import xyz.ficdev.privatepaste.ui.BrandMarkSize
import xyz.ficdev.privatepaste.ui.asString

private val FieldShape = RoundedCornerShape(10.dp)

@Composable
fun UnlockScreen(vm: UnlockViewModel) {
    val state by vm.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val prompt = stringResource(R.string.vault_scan_prompt)
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result -> vm.onScanned(result.contents) }
    val options = {
        ScanOptions()
            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            .setPrompt(prompt)
            .setBeepEnabled(false)
            .setOrientationLocked(false)
    }
    // The camera permission is asked for only here, when Scan is tapped.
    val camera = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) scanner.launch(options()) else vm.onCameraDenied()
    }
    UnlockContent(
        state = state,
        onPassphraseChange = vm::onPassphraseChange,
        onRepeatChange = vm::onRepeatChange,
        onUnlock = vm::unlock,
        onSetUp = vm::setUp,
        onScan = {
            if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
                scanner.launch(options())
            } else {
                camera.launch(Manifest.permission.CAMERA)
            }
        },
        onRetry = vm::retry,
        onSignOut = vm::signOut,
    )
}

@Composable
fun UnlockContent(
    state: UnlockState,
    onPassphraseChange: (String) -> Unit,
    onRepeatChange: (String) -> Unit,
    onUnlock: () -> Unit,
    onSetUp: () -> Unit,
    onScan: () -> Unit,
    onRetry: () -> Unit,
    onSignOut: () -> Unit,
) {
    val colors = MaterialTheme.colorScheme
    Surface(modifier = Modifier.fillMaxSize(), color = colors.background) {
        Box(
            modifier = Modifier
                .safeDrawingPadding()
                .imePadding()
                .verticalScroll(rememberScrollState()),
            contentAlignment = Alignment.TopCenter,
        ) {
            Column(
                modifier = Modifier
                    .widthIn(max = 420.dp)
                    .fillMaxWidth()
                    .padding(horizontal = 24.dp, vertical = 40.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                BrandMark(BrandMarkSize.Large)
                Spacer(Modifier.height(10.dp))

                when (state.vault) {
                    VaultState.LOCKED -> Unlock(state, onPassphraseChange, onUnlock, onScan)
                    VaultState.NEEDS_OWNER_SETUP -> SetUp(state, onPassphraseChange, onRepeatChange, onSetUp)
                    VaultState.SERVER_TOO_OLD -> Blocked(R.string.vault_server_too_old, onRetry)
                    VaultState.UPDATE_REQUIRED -> Blocked(R.string.vault_update_required, onRetry)
                    VaultState.WAITING_FOR_OWNER -> Blocked(R.string.vault_waiting_for_owner, onRetry)
                    VaultState.UNKNOWN, VaultState.UNLOCKED -> Connecting()
                }

                Spacer(Modifier.height(8.dp))
                TextButton(onClick = onSignOut, modifier = Modifier.heightIn(min = 48.dp)) {
                    Text(stringResource(R.string.vault_sign_out), color = colors.onSurfaceVariant)
                }
            }
        }
    }
}

@Composable
private fun Unlock(state: UnlockState, onPassphraseChange: (String) -> Unit, onUnlock: () -> Unit, onScan: () -> Unit) {
    Title(R.string.vault_unlock_title)
    if (state.keyWasReset) Message(stringResource(R.string.vault_was_reset))
    PassphraseField(
        value = state.passphrase,
        onValueChange = onPassphraseChange,
        label = R.string.vault_passphrase,
        enabled = !state.busy,
        imeAction = ImeAction.Go,
        onGo = onUnlock,
    )
    ErrorText(state)
    PrimaryButton(R.string.vault_unlock, enabled = state.canUnlock, busy = state.busy, onClick = onUnlock)
    OrDivider()
    OutlinedButton(
        onClick = onScan,
        enabled = !state.busy,
        shape = FieldShape,
        modifier = Modifier
            .fillMaxWidth()
            .height(52.dp),
    ) {
        Icon(painterResource(R.drawable.ic_scan), contentDescription = null, modifier = Modifier.size(20.dp))
        Spacer(Modifier.size(10.dp))
        Text(stringResource(R.string.vault_scan))
    }
}

@Composable
private fun SetUp(
    state: UnlockState,
    onPassphraseChange: (String) -> Unit,
    onRepeatChange: (String) -> Unit,
    onSetUp: () -> Unit,
) {
    Title(R.string.vault_setup_title)
    PassphraseField(state.passphrase, onPassphraseChange, R.string.vault_passphrase, enabled = !state.busy, imeAction = ImeAction.Next)
    PassphraseField(
        value = state.repeat,
        onValueChange = onRepeatChange,
        label = R.string.vault_repeat_passphrase,
        enabled = !state.busy,
        imeAction = ImeAction.Go,
        onGo = onSetUp,
    )
    Text(
        text = stringResource(R.string.vault_rule),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.fillMaxWidth(),
    )
    ErrorText(state)
    PrimaryButton(R.string.vault_set_up, enabled = state.canSetUp, busy = state.busy, onClick = onSetUp)
}

/** Something stands in the way of syncing, and only someone else (or an update) can move it. */
@Composable
private fun Blocked(message: Int, onRetry: () -> Unit) {
    Message(stringResource(message))
    OutlinedButton(onClick = onRetry, shape = FieldShape, modifier = Modifier.heightIn(min = 48.dp)) {
        Text(stringResource(R.string.vault_try_again))
    }
}

@Composable
private fun Connecting() {
    CircularProgressIndicator(modifier = Modifier.size(28.dp), strokeWidth = 2.5.dp)
    Text(
        text = stringResource(R.string.status_connecting),
        style = MaterialTheme.typography.bodyLarge,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

@Composable
private fun Title(text: Int) {
    Text(stringResource(text), style = MaterialTheme.typography.titleLarge, color = MaterialTheme.colorScheme.onBackground)
}

@Composable
private fun Message(text: String) {
    Text(
        text = text,
        style = MaterialTheme.typography.bodyLarge,
        color = MaterialTheme.colorScheme.onSurface,
        textAlign = TextAlign.Center,
        modifier = Modifier
            .fillMaxWidth()
            .semantics { liveRegion = LiveRegionMode.Polite },
    )
}

@Composable
private fun ErrorText(state: UnlockState) {
    state.error?.let { error ->
        Text(
            text = error.asString(),
            color = MaterialTheme.colorScheme.error,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier
                .fillMaxWidth()
                .semantics { liveRegion = LiveRegionMode.Polite },
        )
    }
}

@Composable
private fun OrDivider() {
    val colors = MaterialTheme.colorScheme
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Box(Modifier.weight(1f).height(1.dp).background(colors.outline))
        Text(stringResource(R.string.vault_or), style = MaterialTheme.typography.bodySmall, color = colors.onSurfaceVariant)
        Box(Modifier.weight(1f).height(1.dp).background(colors.outline))
    }
}

/** A passphrase field with a show/hide button. Never autocorrected or suggested: the keyboard must not learn it. */
@Composable
internal fun PassphraseField(
    value: String,
    onValueChange: (String) -> Unit,
    label: Int,
    enabled: Boolean,
    imeAction: ImeAction,
    onGo: () -> Unit = {},
) {
    var visible by rememberSaveable { mutableStateOf(false) }
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(stringResource(label)) },
        singleLine = true,
        enabled = enabled,
        shape = FieldShape,
        visualTransformation = if (visible) VisualTransformation.None else PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(
            keyboardType = KeyboardType.Password,
            autoCorrectEnabled = false,
            imeAction = imeAction,
        ),
        keyboardActions = KeyboardActions(onGo = { onGo() }),
        trailingIcon = {
            IconButton(onClick = { visible = !visible }) {
                Icon(
                    painter = painterResource(if (visible) R.drawable.ic_visibility_off else R.drawable.ic_visibility),
                    contentDescription = stringResource(if (visible) R.string.vault_hide_passphrase else R.string.vault_show_passphrase),
                    modifier = Modifier.size(20.dp),
                )
            }
        },
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
private fun PrimaryButton(text: Int, enabled: Boolean, busy: Boolean, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        enabled = enabled,
        shape = FieldShape,
        modifier = Modifier
            .fillMaxWidth()
            .height(52.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            if (busy) {
                CircularProgressIndicator(
                    modifier = Modifier.size(18.dp),
                    strokeWidth = 2.dp,
                    color = LocalContentColor.current,
                )
            }
            Text(stringResource(text))
        }
    }
}
