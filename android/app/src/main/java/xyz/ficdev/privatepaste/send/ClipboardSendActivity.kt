package xyz.ficdev.privatepaste.send

import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import xyz.ficdev.privatepaste.clipboard.ClipboardContent
import xyz.ficdev.privatepaste.clipboard.ClipboardReader
import java.io.FileNotFoundException

/**
 * Transparent activity the "Send clipboard" tile opens. Android 10+ lets only the focused app read the clipboard,
 * so it reads in [onWindowFocusChanged] (never earlier), sends, toasts and finishes.
 */
class ClipboardSendActivity : ComponentActivity() {
    private var handled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if ((application as? SenderProvider)?.clipSender == null) {
            handled = true
            toast("Sign in to PrivatePaste first")
            finish()
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || handled) return
        handled = true
        val sender = (application as? SenderProvider)?.clipSender
        if (sender == null) {
            toast("Sign in to PrivatePaste first")
            finish()
            return
        }
        val content = ClipboardReader(this).read()
        if (content == null) {
            toast("Clipboard is empty")
            finish()
            return
        }
        lifecycleScope.launch {
            val result = when (content) {
                is ClipboardContent.Text -> sender.sendText(content.text)
                is ClipboardContent.Image -> sender.sendImage(
                    { contentResolver.openInputStream(content.uri) ?: throw FileNotFoundException(content.uri.toString()) },
                    content.mime,
                )
            }
            toast(result.message)
            finish()
        }
    }

    private fun toast(message: String) = Toast.makeText(applicationContext, message, Toast.LENGTH_SHORT).show()
}
