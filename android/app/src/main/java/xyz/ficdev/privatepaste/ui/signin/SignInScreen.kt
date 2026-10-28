package xyz.ficdev.privatepaste.ui.signin

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
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
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.ui.BrandMark
import xyz.ficdev.privatepaste.ui.BrandMarkSize
import xyz.ficdev.privatepaste.ui.asString

@Composable
fun SignInScreen(vm: SignInViewModel) {
    val state by vm.state.collectAsStateWithLifecycle()
    SignInContent(
        state = state,
        onUrlChange = vm::onUrlChange,
        onPasswordChange = vm::onPasswordChange,
        onDeviceNameChange = vm::onDeviceNameChange,
        onSubmit = vm::submit,
    )
}

@Composable
fun SignInContent(
    state: SignInViewModel.State,
    onUrlChange: (String) -> Unit,
    onPasswordChange: (String) -> Unit,
    onDeviceNameChange: (String) -> Unit,
    onSubmit: () -> Unit,
) {
    var showPassword by rememberSaveable { mutableStateOf(false) }
    val fieldShape = RoundedCornerShape(10.dp)

    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
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

                OutlinedTextField(
                    value = state.url,
                    onValueChange = onUrlChange,
                    label = { Text(stringResource(R.string.signin_server)) },
                    placeholder = { Text("https://") },
                    singleLine = true,
                    enabled = !state.busy,
                    shape = fieldShape,
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Uri,
                        autoCorrectEnabled = false,
                        imeAction = ImeAction.Next,
                    ),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = state.password,
                    onValueChange = onPasswordChange,
                    label = { Text(stringResource(R.string.signin_password)) },
                    singleLine = true,
                    enabled = !state.busy,
                    shape = fieldShape,
                    visualTransformation = if (showPassword) VisualTransformation.None else PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Next),
                    trailingIcon = {
                        IconButton(onClick = { showPassword = !showPassword }) {
                            Icon(
                                painter = painterResource(if (showPassword) R.drawable.ic_visibility_off else R.drawable.ic_visibility),
                                contentDescription = stringResource(
                                    if (showPassword) R.string.signin_hide_password else R.string.signin_show_password,
                                ),
                                modifier = Modifier.size(20.dp),
                            )
                        }
                    },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = state.deviceName,
                    onValueChange = onDeviceNameChange,
                    label = { Text(stringResource(R.string.signin_device_name)) },
                    singleLine = true,
                    enabled = !state.busy,
                    shape = fieldShape,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Go),
                    keyboardActions = KeyboardActions(onGo = { onSubmit() }),
                    modifier = Modifier.fillMaxWidth(),
                )

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

                Button(
                    onClick = onSubmit,
                    enabled = state.canSubmit,
                    shape = fieldShape,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp),
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        if (state.busy) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(18.dp),
                                strokeWidth = 2.dp,
                                color = LocalContentColor.current,   // the button's content color (onPrimary, or disabled)
                            )
                        }
                        Text(stringResource(R.string.signin_button))
                    }
                }
            }
        }
    }
}
