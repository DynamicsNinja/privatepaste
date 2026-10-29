package xyz.ficdev.privatepaste.ui.history

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.ui.RelativeTime
import xyz.ficdev.privatepaste.ui.theme.Live

private val RowShape = RoundedCornerShape(14.dp)
private val TileShape = RoundedCornerShape(10.dp)

/**
 * One history row, like the desktop popup: a type tile, a one-line preview (or a 48 dp thumbnail for images), then
 * "{device} · {time}"; a pinned clip also shows the pin mark at the end. The newest clip gets the accent border and soft glow; [isLive] adds the live dot.
 */
@Composable
fun ClipRow(
    clip: ClipDto,
    relativeTime: RelativeTime,
    isNewest: Boolean,
    isLive: Boolean,
    thumbnails: ThumbnailLoader?,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
    modifier: Modifier = Modifier,
    /** Changes once a minute so the relative time is recomputed. */
    minuteTick: Int = 0,
) {
    val colors = MaterialTheme.colorScheme
    val glow = if (isNewest) Modifier.border(4.dp, colors.primary.copy(alpha = 0.16f), RoundedCornerShape(17.dp)) else Modifier
    val time = remember(clip.lastCopiedAt, minuteTick) { relativeTime.format(clip.lastCopiedAt) }
    val device = clip.sourceDeviceName?.takeIf { it.isNotBlank() } ?: stringResource(R.string.history_unknown_device)

    Box(modifier = modifier.fillMaxWidth().then(glow).padding(3.dp)) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 56.dp)
                .clip(RowShape)
                .background(colors.surface)
                .border(1.dp, if (isNewest) colors.primary else colors.outline, RowShape)
                .combinedClickable(
                    onClickLabel = stringResource(R.string.history_copy_action),
                    onLongClickLabel = stringResource(R.string.history_menu_action),
                    onLongClick = onLongClick,
                    onClick = onClick,
                )
                .padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            KindTile(clip.kind)
            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                if (clip.kind == ClipKind.IMAGE) {
                    Thumbnail(clip, thumbnails)
                } else {
                    Text(
                        text = preview(clip.text),
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.onSurface,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(5.dp),
                ) {
                    PlatformBadge(clip.sourceDevicePlatform)
                    Text(
                        text = stringResource(R.string.history_meta, device, time),
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            if (clip.pinnedAt != null) {
                Icon(
                    painter = painterResource(R.drawable.ic_pin),
                    contentDescription = stringResource(R.string.history_pinned_mark),
                    tint = colors.primary,
                    modifier = Modifier.size(16.dp),
                )
            }
            if (isLive) {
                val live = stringResource(R.string.history_live)
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .clip(CircleShape)
                        .background(Live)
                        .semantics { contentDescription = live },
                )
            }
        }
    }
}

@Composable
private fun KindTile(kind: ClipKind) {
    val (icon, label) = when (kind) {
        ClipKind.TEXT -> R.drawable.ic_kind_text to R.string.history_kind_text
        ClipKind.LINK -> R.drawable.ic_kind_link to R.string.history_kind_link
        ClipKind.IMAGE -> R.drawable.ic_kind_image to R.string.history_kind_image
    }
    Box(
        modifier = Modifier
            .size(36.dp)
            .clip(TileShape)
            .background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            painter = painterResource(icon),
            contentDescription = stringResource(label),
            tint = MaterialTheme.colorScheme.primary,
            modifier = Modifier.size(18.dp),
        )
    }
}

/** The source device's platform icon, muted and about the cap height of the meta text. */
@Composable
private fun PlatformBadge(platform: String?) {
    val icon = PlatformIcon.forPlatform(platform)
    Icon(
        painter = painterResource(icon.drawable),
        contentDescription = stringResource(icon.label),
        tint = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.size(13.dp),
    )
}

@Composable
private fun Thumbnail(clip: ClipDto, thumbnails: ThumbnailLoader?) {
    val bitmap: ImageBitmap? by produceState(thumbnails?.peek(clip.hash), clip.hash, thumbnails) {
        if (value == null && thumbnails != null) value = thumbnails.load(clip)
    }
    val shape = TileShape
    Box(
        modifier = Modifier
            .size(48.dp)
            .clip(shape)
            .background(MaterialTheme.colorScheme.surfaceVariant),
    ) {
        bitmap?.let {
            Image(
                bitmap = it,
                contentDescription = null,   // the type tile already says "Image"
                contentScale = ContentScale.Crop,
                modifier = Modifier.size(48.dp),
            )
        }
    }
}

/** First non-empty line, trimmed (the desktop popup's preview). The Text composable ellipsizes the rest. */
internal fun preview(text: String?): String =
    text.orEmpty().lineSequence().map { it.trim() }.firstOrNull { it.isNotEmpty() }.orEmpty().take(PREVIEW_MAX)

private const val PREVIEW_MAX = 200
