package xyz.ficdev.privatepaste.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val NightVaultDark = darkColorScheme(
    primary = AccentDark,
    onPrimary = NightDark,
    primaryContainer = RaisedDark,
    onPrimaryContainer = AccentDark,
    secondary = AccentDark,
    onSecondary = NightDark,
    secondaryContainer = RaisedDark,
    onSecondaryContainer = TextDark,
    tertiary = Live,
    onTertiary = NightDark,
    background = NightDark,
    onBackground = TextDark,
    surface = SurfaceDark,
    onSurface = TextDark,
    surfaceVariant = RaisedDark,
    onSurfaceVariant = MutedDark,
    surfaceContainerLowest = NightDark,
    surfaceContainerLow = SurfaceDark,
    surfaceContainer = SurfaceDark,
    surfaceContainerHigh = RaisedDark,
    surfaceContainerHighest = RaisedDark,
    outline = LineDark,
    outlineVariant = LineDark,
    error = DangerDark,
    onError = NightDark,
)

val NightVaultLight = lightColorScheme(
    primary = AccentLight,
    onPrimary = Color.White,
    primaryContainer = RaisedLight,
    onPrimaryContainer = AccentLight,
    secondary = AccentLight,
    onSecondary = Color.White,
    secondaryContainer = RaisedLight,
    onSecondaryContainer = TextLight,
    tertiary = Live,
    onTertiary = TextLight,
    background = NightLight,
    onBackground = TextLight,
    surface = SurfaceLight,
    onSurface = TextLight,
    surfaceVariant = RaisedLight,
    onSurfaceVariant = MutedLight,
    surfaceContainerLowest = SurfaceLight,
    surfaceContainerLow = SurfaceLight,
    surfaceContainer = SurfaceLight,
    surfaceContainerHigh = RaisedLight,
    surfaceContainerHighest = RaisedLight,
    outline = LineLight,
    outlineVariant = LineLight,
    error = DangerLight,
    onError = Color.White,
)

/** Night vault theme: dark by default, light when the system is in light mode. */
@Composable
fun PrivatePasteTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    MaterialTheme(
        colorScheme = if (darkTheme) NightVaultDark else NightVaultLight,
        typography = PrivatePasteTypography,
        content = content,
    )
}
