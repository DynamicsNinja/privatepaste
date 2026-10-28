package xyz.ficdev.privatepaste.ui

import android.content.Context
import androidx.annotation.StringRes
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext

/** Text a ViewModel hands to the screen: a string resource, or a message that is already final (server copy). */
sealed interface UiText {
    data class Res(@param:StringRes val id: Int) : UiText
    data class Raw(val text: String) : UiText

    fun resolve(context: Context): String = when (this) {
        is Res -> context.getString(id)
        is Raw -> text
    }
}

@Composable
fun UiText.asString(): String = resolve(LocalContext.current)
