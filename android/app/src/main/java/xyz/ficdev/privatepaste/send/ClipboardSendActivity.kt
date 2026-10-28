package xyz.ficdev.privatepaste.send

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.clipboard.ClipboardContent
import xyz.ficdev.privatepaste.clipboard.ClipboardReader
import java.io.FileNotFoundException

/**
 * Transparent activity the "Send clipboard" tile opens. Android 10+ lets only the focused app read the clipboard,
 * so it reads in [onWindowFocusChanged] (never earlier). An image is read here while the URI grant lasts; the
 * upload runs in [SenderProvider.sendScope] and this activity finishes right after the hand-off.
 */
class ClipboardSendActivity : ComponentActivity() {
    private var handled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if ((application as? SenderProvider)?.clipSender == null) {
            handled = true
            showToast(applicationContext, getString(R.string.sign_in_first))
            finish()
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || handled) return
        handled = true
        val provider = application as? SenderProvider
        val sender = provider?.clipSender
        if (provider == null || sender == null) {
            showToast(applicationContext, getString(R.string.sign_in_first))
            finish()
            return
        }
        val content = ClipboardReader(this).read()
        if (content == null) {
            showToast(applicationContext, "Clipboard is empty")
            finish()
            return
        }
        lifecycleScope.launch {
            val send: suspend () -> SendResult = when (content) {
                is ClipboardContent.Text -> ({ sender.sendText(content.text) })
                is ClipboardContent.Image -> {
                    val resolver = contentResolver
                    val uri = content.uri
                    if (!uri.isSendable()) {
                        ({ SendResult.Rejected(ClipSender.UNREADABLE_IMAGE) })
                    } else {
                        val read = sender.readImage { resolver.openInputStream(uri) ?: throw FileNotFoundException(uri.toString()) }
                        val failure = read.toFailure()
                        if (failure != null) ({ failure }) else {
                            val bytes = (read as ImageRead.Bytes).bytes
                            ({ sender.sendImageBytes(bytes, content.mime) })
                        }
                    }
                }
            }
            handOff(applicationContext, provider.sendScope, send)
            finish()
        }
    }
}
