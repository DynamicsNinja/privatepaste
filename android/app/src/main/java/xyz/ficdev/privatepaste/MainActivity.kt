package xyz.ficdev.privatepaste

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import xyz.ficdev.privatepaste.ui.AppNav
import xyz.ficdev.privatepaste.ui.UiDepsProvider
import xyz.ficdev.privatepaste.ui.theme.PrivatePasteTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val deps = (application as UiDepsProvider).uiDeps
        setContent {
            PrivatePasteTheme {
                AppNav(deps)
            }
        }
    }
}
