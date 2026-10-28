package xyz.ficdev.privatepaste.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import xyz.ficdev.privatepaste.R

private fun soraWeight(weight: FontWeight) = Font(
    resId = R.font.sora,
    weight = weight,
    variationSettings = FontVariation.Settings(FontVariation.weight(weight.weight)),
)

/** Sora (SIL OFL), bundled as a variable font: 300 wordmark accent, 400 body, 600 headings and buttons. */
val Sora = FontFamily(
    soraWeight(FontWeight.Light),
    soraWeight(FontWeight.Normal),
    soraWeight(FontWeight.Medium),
    soraWeight(FontWeight.SemiBold),
    soraWeight(FontWeight.Bold),
)

private val base = Typography()

private fun TextStyle.sora(weight: FontWeight) = copy(fontFamily = Sora, fontWeight = weight)

val PrivatePasteTypography = Typography(
    displayLarge = base.displayLarge.sora(FontWeight.SemiBold),
    displayMedium = base.displayMedium.sora(FontWeight.SemiBold),
    displaySmall = base.displaySmall.sora(FontWeight.SemiBold),
    headlineLarge = base.headlineLarge.sora(FontWeight.SemiBold),
    headlineMedium = base.headlineMedium.sora(FontWeight.SemiBold),
    headlineSmall = base.headlineSmall.sora(FontWeight.SemiBold),
    titleLarge = base.titleLarge.sora(FontWeight.SemiBold),
    titleMedium = base.titleMedium.sora(FontWeight.SemiBold),
    titleSmall = base.titleSmall.sora(FontWeight.SemiBold),
    bodyLarge = base.bodyLarge.sora(FontWeight.Normal),
    bodyMedium = base.bodyMedium.sora(FontWeight.Normal),
    bodySmall = base.bodySmall.sora(FontWeight.Normal),
    labelLarge = base.labelLarge.sora(FontWeight.SemiBold),
    labelMedium = base.labelMedium.sora(FontWeight.SemiBold),
    labelSmall = base.labelSmall.sora(FontWeight.Normal),
)
