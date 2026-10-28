package xyz.ficdev.privatepaste.send

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import java.io.FileNotFoundException

/** Share-sheet target: sends the shared text or image, shows the outcome in a toast and finishes. */
class ShareActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val sender = (application as? SenderProvider)?.clipSender
        if (sender == null) {
            toast("Sign in to PrivatePaste first")
            finish()
            return
        }
        val text = intent.getStringExtra(Intent.EXTRA_TEXT)
        val mime = intent.type.orEmpty()
        val stream = streamExtra()
        lifecycleScope.launch {
            val message = when {
                mime.startsWith("image/") && stream != null -> sender.sendImage(
                    { contentResolver.openInputStream(stream) ?: throw FileNotFoundException(stream.toString()) },
                    mime.takeIf { it != "image/*" } ?: (contentResolver.getType(stream) ?: "image/png"),
                ).message
                !text.isNullOrEmpty() -> sender.sendText(text).message
                else -> "Nothing to send"
            }
            toast(message)
            finish()
        }
    }

    @Suppress("DEPRECATION")
    private fun streamExtra(): Uri? =
        if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        else intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri

    private fun toast(message: String) = Toast.makeText(applicationContext, message, Toast.LENGTH_SHORT).show()
}
