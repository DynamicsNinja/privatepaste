package xyz.ficdev.privatepaste.ui.history

import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filter
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.BrandMark
import xyz.ficdev.privatepaste.ui.BrandMarkSize
import xyz.ficdev.privatepaste.ui.RelativeTime
import xyz.ficdev.privatepaste.ui.theme.Live

/** The history screen's top bar (brand mark, connection chip, settings button); for tests and screenshots. */
const val HISTORY_TOP_BAR_TAG = "history_top_bar"

@Composable
fun HistoryScreen(
    vm: HistoryViewModel,
    thumbnails: ThumbnailLoader?,
    relativeTime: RelativeTime,
    onOpenSettings: () -> Unit,
) {
    val state by vm.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    LaunchedEffect(vm) {
        vm.messages.collect { Toast.makeText(context, it.resolve(context), Toast.LENGTH_SHORT).show() }
    }
    // Re-renders the relative times once a minute.
    val minuteTick by produceState(0) {
        while (true) {
            delay(60_000)
            value++
        }
    }
    HistoryContent(
        state = state,
        relativeTime = relativeTime,
        onQueryChange = vm::onQueryChange,
        onCopy = vm::copy,
        onDelete = vm::delete,
        onTab = vm::onTab,
        onTogglePin = vm::togglePin,
        onNearEnd = vm::onNearEnd,
        onOpenSettings = onOpenSettings,
        thumbnails = thumbnails,
        minuteTick = minuteTick,
    )
}

@Composable
fun HistoryContent(
    state: HistoryState,
    relativeTime: RelativeTime,
    onQueryChange: (String) -> Unit,
    onCopy: (ClipDto) -> Unit,
    onDelete: (ClipDto) -> Unit,
    onNearEnd: () -> Unit,
    onOpenSettings: () -> Unit,
    thumbnails: ThumbnailLoader? = null,
    minuteTick: Int = 0,
    onTab: (HistoryTab) -> Unit = {},
    onTogglePin: (ClipDto) -> Unit = {},
) {
    var pendingDelete by remember { mutableStateOf<ClipDto?>(null) }
    // The clip whose long-press menu is open, by id so the menu survives list updates.
    var menuFor by remember { mutableStateOf<String?>(null) }
    val listState = rememberLazyListState()

    LaunchedEffect(listState) {
        snapshotFlow {
            val info = listState.layoutInfo
            val last = info.visibleItemsInfo.lastOrNull()?.index ?: -1
            (info.totalItemsCount > 0 && last >= info.totalItemsCount - NEAR_END) to info.totalItemsCount
        }
            .distinctUntilChanged()
            .filter { (nearEnd, _) -> nearEnd }
            .collect { onNearEnd() }
    }

    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(modifier = Modifier.safeDrawingPadding()) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .testTag(HISTORY_TOP_BAR_TAG)
                    .padding(start = 16.dp, end = 4.dp, top = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                BrandMark(BrandMarkSize.TopBar)
                Spacer(Modifier.weight(1f))
                StatusChip(state.syncState)
                IconButton(onClick = onOpenSettings) {
                    Icon(
                        painter = painterResource(R.drawable.ic_settings),
                        contentDescription = stringResource(R.string.history_settings),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            SearchField(
                query = state.query,
                onQueryChange = onQueryChange,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
            )

            TabRow(state.tab, onTab, modifier = Modifier.padding(start = 16.dp, end = 16.dp, bottom = 8.dp))

            if (state.items.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(32.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    if (state.loadingMore) {
                        CircularProgressIndicator()
                    } else {
                        Text(
                            text = stringResource(
                                when {
                                    state.query.isNotBlank() -> R.string.history_no_matches
                                    state.tab == HistoryTab.PINNED -> R.string.history_pinned_empty
                                    else -> R.string.history_empty
                                },
                            ),
                            style = MaterialTheme.typography.bodyLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            textAlign = TextAlign.Center,
                            modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                        )
                    }
                }
            } else {
                LazyColumn(
                    state = listState,
                    contentPadding = PaddingValues(start = 13.dp, end = 13.dp, bottom = 16.dp),
                    verticalArrangement = Arrangement.spacedBy(5.dp),
                    modifier = Modifier.fillMaxSize(),
                ) {
                    items(state.items, key = { it.id }) { clip ->
                        Box {
                            ClipRow(
                                clip = clip,
                                relativeTime = relativeTime,
                                isNewest = state.tab == HistoryTab.HISTORY && state.query.isBlank() &&
                                    clip.id == state.items.first().id,
                                isLive = clip.id == state.liveClipId,
                                thumbnails = thumbnails,
                                onClick = { onCopy(clip) },
                                onLongClick = { menuFor = clip.id },
                                minuteTick = minuteTick,
                            )
                            DropdownMenu(expanded = menuFor == clip.id, onDismissRequest = { menuFor = null }) {
                                DropdownMenuItem(
                                    text = {
                                        Text(stringResource(if (clip.pinnedAt == null) R.string.history_pin else R.string.history_unpin))
                                    },
                                    leadingIcon = {
                                        Icon(painterResource(R.drawable.ic_pin), contentDescription = null, modifier = Modifier.size(20.dp))
                                    },
                                    onClick = {
                                        menuFor = null
                                        onTogglePin(clip)
                                    },
                                )
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.history_delete), color = MaterialTheme.colorScheme.error) },
                                    onClick = {
                                        menuFor = null
                                        pendingDelete = clip
                                    },
                                )
                            }
                        }
                    }
                    if (state.loadingMore) {
                        item(key = "loading") {
                            Box(Modifier.fillMaxWidth().padding(12.dp), contentAlignment = Alignment.Center) {
                                CircularProgressIndicator(modifier = Modifier.size(24.dp), strokeWidth = 2.dp)
                            }
                        }
                    }
                }
            }
        }
    }

    pendingDelete?.let { clip ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            text = { Text(stringResource(R.string.history_delete_title), style = MaterialTheme.typography.bodyLarge) },
            confirmButton = {
                TextButton(onClick = {
                    pendingDelete = null
                    onDelete(clip)
                }) {
                    Text(stringResource(R.string.history_delete), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { pendingDelete = null }) { Text(stringResource(R.string.history_cancel)) }
            },
        )
    }
}

/** History / Pinned switch under the search field. */
@Composable
private fun TabRow(selected: HistoryTab, onSelect: (HistoryTab) -> Unit, modifier: Modifier = Modifier) {
    Row(modifier = modifier.selectableGroup(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        TabChip(R.string.history_tab_history, selected == HistoryTab.HISTORY) { onSelect(HistoryTab.HISTORY) }
        TabChip(R.string.history_tab_pinned, selected == HistoryTab.PINNED) { onSelect(HistoryTab.PINNED) }
    }
}

@Composable
private fun TabChip(label: Int, selected: Boolean, onClick: () -> Unit) {
    val colors = MaterialTheme.colorScheme
    val shape = RoundedCornerShape(50)
    Box(
        modifier = Modifier
            .heightIn(min = 40.dp)   // touch target
            .clip(shape)
            .background(if (selected) colors.primary.copy(alpha = 0.16f) else Color.Transparent)
            .border(1.dp, if (selected) colors.primary else colors.outline, shape)
            .selectable(selected = selected, role = Role.Tab, onClick = onClick)
            .padding(horizontal = 16.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = stringResource(label),
            style = MaterialTheme.typography.labelLarge,
            color = if (selected) colors.primary else colors.onSurfaceVariant,
        )
    }
}

@Composable
private fun SearchField(query: String, onQueryChange: (String) -> Unit, modifier: Modifier = Modifier) {
    val colors = MaterialTheme.colorScheme
    OutlinedTextField(
        value = query,
        onValueChange = onQueryChange,
        placeholder = { Text(stringResource(R.string.history_search)) },
        singleLine = true,
        shape = RoundedCornerShape(10.dp),
        leadingIcon = {
            Icon(painterResource(R.drawable.ic_search), contentDescription = null, modifier = Modifier.size(20.dp))
        },
        trailingIcon = if (query.isEmpty()) null else {
            {
                IconButton(onClick = { onQueryChange("") }) {
                    Icon(
                        painter = painterResource(R.drawable.ic_close),
                        contentDescription = stringResource(R.string.history_clear_search),
                        modifier = Modifier.size(20.dp),
                    )
                }
            }
        },
        colors = OutlinedTextFieldDefaults.colors(
            focusedContainerColor = colors.surfaceVariant,
            unfocusedContainerColor = colors.surfaceVariant,
            unfocusedBorderColor = colors.outline,
        ),
        modifier = modifier.fillMaxWidth(),
    )
}

@Composable
private fun StatusChip(sync: SyncState) {
    val colors = MaterialTheme.colorScheme
    val (label, dot) = when (sync) {
        SyncState.CONNECTED -> R.string.status_connected to Live
        SyncState.CONNECTING -> R.string.status_connecting to colors.onSurfaceVariant
        SyncState.OFFLINE, SyncState.SIGNED_OUT -> R.string.status_offline to colors.outline
    }
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(colors.surfaceVariant)
            .padding(horizontal = 10.dp, vertical = 5.dp)
            .semantics(mergeDescendants = true) { liveRegion = LiveRegionMode.Polite },
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Box(Modifier.size(7.dp).clip(CircleShape).background(dot))
        Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = colors.onSurface)
    }
}

private const val NEAR_END = 5
