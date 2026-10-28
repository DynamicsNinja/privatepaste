package xyz.ficdev.privatepaste.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import xyz.ficdev.privatepaste.R

/** How [BrandMark] is laid out: the large sign-in mark, or the small one in the history top bar. */
enum class BrandMarkSize(val logo: Dp, val wordmark: TextUnit, val gap: Dp, val stacked: Boolean) {
    /** Logo above the wordmark, centered (sign-in screen). */
    Large(logo = 72.dp, wordmark = 30.sp, gap = 14.dp, stacked = true),

    /** Logo tile beside the wordmark, sized for a top bar. */
    TopBar(logo = 32.dp, wordmark = 21.sp, gap = 10.dp, stacked = false),
}

/**
 * The keyhole logo tile and the two-tone wordmark (branding/README.md): `private` semibold in the text color, then
 * `paste` light in the accent color. Read by accessibility services as one heading, "PrivatePaste".
 */
@Composable
fun BrandMark(size: BrandMarkSize, modifier: Modifier = Modifier) {
    val name = stringResource(R.string.app_name)
    val semantics = modifier
        .testTag(BRAND_MARK_TAG)
        .clearAndSetSemantics {
            contentDescription = name
            heading()
        }
    if (size.stacked) {
        Column(
            modifier = semantics,
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(size.gap),
        ) {
            Logo(size.logo)
            Wordmark(size.wordmark, tight = false)
        }
    } else {
        Row(
            modifier = semantics,
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(size.gap),
        ) {
            Logo(size.logo)
            // A tight line height: the row is as tall as the logo tile, not the headline's 36 sp line.
            Wordmark(size.wordmark, tight = true)
        }
    }
}

const val BRAND_MARK_TAG = "brand_mark"

@Composable
private fun Logo(size: Dp) {
    Image(
        painter = painterResource(R.drawable.ic_logo),
        contentDescription = null,
        modifier = Modifier.size(size),
    )
}

@Composable
private fun Wordmark(fontSize: TextUnit, tight: Boolean) {
    val colors = MaterialTheme.colorScheme
    Text(
        text = buildAnnotatedString {
            withStyle(SpanStyle(fontWeight = FontWeight.SemiBold, color = colors.onBackground)) { append("private") }
            withStyle(SpanStyle(fontWeight = FontWeight.Light, color = colors.primary)) { append("paste") }
        },
        style = MaterialTheme.typography.headlineMedium.let {
            if (tight) it.copy(fontSize = fontSize, lineHeight = fontSize * 1.25f) else it.copy(fontSize = fontSize)
        },
        maxLines = 1,
    )
}
