package xyz.ficdev.privatepaste.ui.settings

import android.graphics.Bitmap
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
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
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import kotlinx.coroutines.delay
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.ui.asString
import java.time.Clock
import java.time.Duration
import java.util.Locale

/** `Add a device`: the one-time code, counting down; at 0 it hides and offers a new one. */
@Composable
fun AddDeviceScreen(
    state: AddDeviceState,
    onNewCode: () -> Unit,
    onExpired: () -> Unit,
    onBack: () -> Unit,
    clock: Clock = Clock.systemUTC(),
) {
    BackHandler(onBack = onBack)
    val expiresAt = state.expiresAt?.takeIf { state.qrText != null }
    // Ticks only while a code is shown (not while loading or after a failure); at 0 the code leaves the state.
    val left by produceState<Duration?>(null, expiresAt) {
        if (expiresAt == null) {
            value = null
            return@produceState
        }
        while (true) {
            val remaining = Duration.between(clock.instant(), expiresAt).coerceAtLeast(Duration.ZERO)
            value = remaining
            if (remaining.isZero) {
                onExpired()
                return@produceState
            }
            delay(1_000)
        }
    }
    AddDeviceContent(state, left, onNewCode, onBack)
}

@Composable
fun AddDeviceContent(state: AddDeviceState, left: Duration?, onNewCode: () -> Unit, onBack: () -> Unit) {
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
                Text(stringResource(R.string.settings_add_device), style = MaterialTheme.typography.titleLarge, color = colors.onBackground)
            }

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 24.dp, vertical = 24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                val qr = state.qrText
                val expired = left != null && left.isZero
                when {
                    state.loading -> CircularProgressIndicator(modifier = Modifier.padding(48.dp))
                    state.error != null -> {
                        Message(state.error.asString(), color = colors.error)
                        NewCodeButton(R.string.vault_try_again, onNewCode)
                    }
                    qr == null || expired -> {
                        Message(stringResource(R.string.vault_code_expired), color = colors.onSurface)
                        NewCodeButton(R.string.add_device_new, onNewCode)
                    }
                    else -> {
                        QrCode(qr)
                        Message(stringResource(R.string.add_device_body), color = colors.onSurface)
                        if (left != null) {
                            Text(
                                text = stringResource(R.string.add_device_left, minutesSeconds(left)),
                                style = MaterialTheme.typography.labelLarge,
                                color = colors.onSurfaceVariant,
                            )
                        }
                    }
                }
            }
        }
    }
}

/** Dark modules on a white card in both themes: scanners expect dark on light, with a quiet zone around it. */
@Composable
private fun QrCode(text: String) {
    val bitmap = remember(text) { qrBitmap(text) }
    Box(
        modifier = Modifier
            .widthIn(max = 280.dp)
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(Color.White)
            .padding(20.dp),
        contentAlignment = Alignment.Center,
    ) {
        Image(
            bitmap = bitmap,
            contentDescription = stringResource(R.string.add_device_qr),
            filterQuality = FilterQuality.None,   // crisp modules when scaled up
            modifier = Modifier.size(240.dp),
        )
    }
}

@Composable
private fun Message(text: String, color: Color) {
    Text(
        text = text,
        style = MaterialTheme.typography.bodyLarge,
        color = color,
        textAlign = TextAlign.Center,
        modifier = Modifier
            .widthIn(max = 360.dp)
            .semantics { liveRegion = LiveRegionMode.Polite },
    )
}

@Composable
private fun NewCodeButton(label: Int, onClick: () -> Unit) {
    Button(onClick = onClick, shape = RoundedCornerShape(10.dp), modifier = Modifier.heightIn(min = 48.dp)) {
        Text(stringResource(label))
    }
}

private fun minutesSeconds(d: Duration): String {
    val s = d.seconds
    return String.format(Locale.ROOT, "%d:%02d", s / 60, s % 60)
}

/** One pixel per module, no margin (the card is the quiet zone); drawn scaled up without filtering. */
internal fun qrBitmap(text: String): ImageBitmap {
    val hints = mapOf(EncodeHintType.MARGIN to 0, EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M)
    val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints)
    val w = matrix.width
    val h = matrix.height
    val pixels = IntArray(w * h) { i -> if (matrix[i % w, i / w]) android.graphics.Color.BLACK else android.graphics.Color.WHITE }
    return Bitmap.createBitmap(pixels, w, h, Bitmap.Config.ARGB_8888).asImageBitmap()
}
