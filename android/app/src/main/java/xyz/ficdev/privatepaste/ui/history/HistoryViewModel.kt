package xyz.ficdev.privatepaste.ui.history

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.api.ApiException
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.core.crypto.ClipCodec
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipPage
import xyz.ficdev.privatepaste.storage.PINNED_FIRST
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.UiText
import kotlin.coroutines.CoroutineContext

/**
 * The server calls the history screen makes, with every clip and image already opened. Production adapts
 * [PrivatePasteApi]; tests pass a fake.
 */
interface ClipRemote {
    suspend fun clips(cursor: String?, limit: Int, pinned: Boolean = false): ClipPage
    suspend fun pin(id: String, pinned: Boolean): ClipDto
    suspend fun restore(id: String): ClipDto?
    suspend fun delete(id: String): Boolean

    /** The thumbnail PNG. */
    suspend fun thumbnail(id: String): ByteArray

    /** The full PNG of an image clip. */
    suspend fun image(id: String): ByteArray
}

/** Opens what the server sends with [codec]: the server holds only ciphertext. */
fun PrivatePasteApi.asClipRemote(codec: ClipCodec): ClipRemote = object : ClipRemote {
    override suspend fun clips(cursor: String?, limit: Int, pinned: Boolean): ClipPage {
        val page = this@asClipRemote.clips(cursor, limit, pinned)
        return page.copy(items = page.items.map(codec::decode))
    }
    override suspend fun pin(id: String, pinned: Boolean) = codec.decode(this@asClipRemote.pin(id, pinned))
    override suspend fun restore(id: String) = this@asClipRemote.restore(id)?.let(codec::decode)
    override suspend fun delete(id: String) = this@asClipRemote.delete(id)
    override suspend fun thumbnail(id: String) = codec.decodeThumb(this@asClipRemote.thumb(id))
    override suspend fun image(id: String) = codec.decodeBlob(this@asClipRemote.blob(id, 0))
}

/** The local clipboard. Production adapts [ClipboardWriter]. */
interface ClipCopier {
    fun writeText(text: String)

    /** @throws java.io.IOException when the image file cannot be written. */
    fun writeImage(clipId: String, png: ByteArray)
}

fun ClipboardWriter.asClipCopier(): ClipCopier = object : ClipCopier {
    override fun writeText(text: String) = this@asClipCopier.writeText(text)
    override fun writeImage(clipId: String, png: ByteArray) = this@asClipCopier.writeImage(clipId, png)
}

enum class HistoryTab { HISTORY, PINNED }

data class HistoryState(
    val tab: HistoryTab = HistoryTab.HISTORY,
    val query: String = "",
    val items: List<ClipDto> = emptyList(),
    /** The newest clip, when this phone applied it to its clipboard: it gets the live dot. */
    val liveClipId: String? = null,
    val syncState: SyncState = SyncState.CONNECTING,
    val loadingMore: Boolean = false,
    /** A search with no hits here: offer to search the rest of the history on the server. */
    val canSearchOlder: Boolean = false,
    val searchingOlder: Boolean = false,
)

/**
 * History list (and its Pinned tab, which lists only pinned clips, newest pinned first): the local cache shows at
 * once, and older server pages follow it, deduped by id, loading with `nextCursor` when the list nears its end (the
 * Pinned tab loads its first page at once: pins can be older than the cache). The server holds only ciphertext, so
 * search runs on this phone: a query filters the cached and loaded clips by their text, ignoring case, and never
 * asks the server by itself. When nothing here matches, "Search older clips" ([searchOlder]) pages through the rest
 * of the tab's history, opening each page, and appends the matches. Taps copy locally first, then bump the clip on
 * the server in the background.
 */
class HistoryViewModel(
    cached: StateFlow<List<ClipDto>>,
    private val lastAppliedHash: () -> String?,
    private val syncState: StateFlow<SyncState>,
    private val remote: () -> ClipRemote?,
    private val copier: ClipCopier,
    /** The full PNG of an image clip when this phone has it locally, else null. */
    private val cachedImage: (clipId: String) -> ByteArray? = { null },
    private val io: CoroutineContext = Dispatchers.IO,
    /** A call got 401: this device was revoked. Routes to the app's sign-out path. */
    private val onUnauthorized: () -> Unit = {},
) : ViewModel() {

    /** Server pages for one tab; [loaded] once the first page arrived. */
    private data class Remote(
        val pinned: Boolean = false,
        val items: List<ClipDto> = emptyList(),
        val next: String? = null,
        val loaded: Boolean = false,
        val loading: Boolean = false,
    )

    /** Matches for [query] from paging through the whole tab on the server; [done] once the last page was read. */
    private data class Older(
        val query: String = "",
        val pinned: Boolean = false,
        val items: List<ClipDto> = emptyList(),
        val searching: Boolean = false,
        val done: Boolean = false,
    )

    /** This phone's own changes, shown before the sync broadcast (or a search page) catches up. */
    private data class Local(val deleted: Set<String> = emptySet(), val updated: Map<String, ClipDto> = emptyMap())

    private val query = MutableStateFlow("")
    private val remotePages = MutableStateFlow(Remote())
    private val local = MutableStateFlow(Local())
    private val older = MutableStateFlow(Older())
    private var pageJob: Job? = null
    private var olderJob: Job? = null

    private val _messages = Channel<UiText>(Channel.BUFFERED)

    /** One-shot toasts ("Copied", "Image not available offline", ...). */
    val messages: Flow<UiText> = _messages.receiveAsFlow()

    val state: StateFlow<HistoryState> =
        combine(cached, query, combine(remotePages, older, ::Pair), local, syncState) { cache, q, (page, old), changes, sync ->
            val needle = q.trim()
            val pinnedTab = page.pinned
            val search = old.takeIf { needle.isNotEmpty() && it.query == needle && it.pinned == pinnedTab }
            // A change this phone made counts until the cache or a server page holds the same or a newer version.
            fun ClipDto.fresh() = changes.updated[id]?.takeIf { it.seq >= seq } ?: this
            val base = cache.map { it.fresh() }
            val fromServer = (page.items + search?.items.orEmpty()).map { it.fresh() }
            val ids = base.mapTo(HashSet()) { it.id }
            val merged = (base + fromServer.filter { ids.add(it.id) })
                .filter { it.id !in changes.deleted && (!pinnedTab || it.pinnedAt != null) && it.matches(needle) }
            val items = if (pinnedTab) merged.sortedWith(PINNED_FIRST) else merged
            val newest = cache.firstOrNull()
            HistoryState(
                tab = if (pinnedTab) HistoryTab.PINNED else HistoryTab.HISTORY,
                query = q,
                items = items,
                liveClipId = newest?.takeIf { it.id !in changes.deleted && it.hash == lastAppliedHash() }?.id,
                syncState = sync,
                loadingMore = page.loading,
                canSearchOlder = needle.isNotEmpty() && items.isEmpty() && search?.searching != true && search?.done != true,
                searchingOlder = search?.searching == true,
            )
        }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), HistoryState(syncState = syncState.value))

    fun onQueryChange(value: String) {
        query.value = value
        if (older.value.query != value.trim()) forgetOlder()
    }

    /** Pages through the tab's whole history on the server, opening each page, and appends what matches the query. */
    fun searchOlder() {
        val needle = query.value.trim()
        if (needle.isEmpty() || older.value.let { it.query == needle && it.searching }) return
        val api = remote()
        if (api == null) {
            _messages.trySend(UiText.Res(R.string.signin_unreachable))
            return
        }
        val tabPinned = remotePages.value.pinned
        olderJob?.cancel()
        older.value = Older(needle, tabPinned, searching = true)
        fun Older.mine() = this.query == needle && this.pinned == tabPinned
        olderJob = viewModelScope.launch {
            try {
                var cursor: String? = null
                do {
                    val page = withContext(io) { api.clips(cursor, OLDER_PAGE_SIZE, tabPinned) }
                    val hits = page.items.filter { it.matches(needle) }
                    if (hits.isNotEmpty()) older.update { if (it.mine()) it.copy(items = it.items + hits) else it }
                    cursor = page.nextCursor
                } while (cursor != null)
                older.update { if (it.mine()) it.copy(searching = false, done = true) else it }
            } catch (e: CancellationException) {
                older.update { if (it.mine()) it.copy(searching = false) else it }
                throw e
            } catch (e: Exception) {
                // Offline or a server error: what was found stays, and the button comes back to retry.
                older.update { if (it.mine()) it.copy(searching = false) else it }
                if (e is ApiException.Unauthorized) onUnauthorized() else _messages.trySend(UiText.Res(R.string.signin_unreachable))
            }
        }
    }

    private fun forgetOlder() {
        olderJob?.cancel()
        older.value = Older()
    }

    fun onTab(tab: HistoryTab) {
        val pinned = tab == HistoryTab.PINNED
        if (remotePages.value.pinned == pinned) return
        pageJob?.cancel()
        forgetOlder()
        remotePages.value = Remote(pinned)
        if (pinned) pageJob = viewModelScope.launch { load(null) }
    }

    /** The list is near its end: load the next server page. While searching, only what is already here is searched. */
    fun onNearEnd() {
        val page = remotePages.value
        if (query.value.isNotBlank() || page.loading) return
        if (page.loaded && page.next == null) return
        pageJob = viewModelScope.launch { load(page.next) }
    }

    private suspend fun load(cursor: String?) {
        val api = remote() ?: return
        val pinned = remotePages.value.pinned
        // The tab may change while a page is in flight; only the tab that asked still wants the answer.
        fun Remote.mine() = this.pinned == pinned
        remotePages.update { if (it.mine()) it.copy(loading = true) else it }
        try {
            val result = withContext(io) { api.clips(cursor, PAGE_SIZE, pinned) }
            remotePages.update {
                if (!it.mine()) it
                else it.copy(items = it.items + result.items, next = result.nextCursor, loaded = true, loading = false)
            }
        } catch (e: CancellationException) {
            remotePages.update { if (it.mine()) it.copy(loading = false) else it }
            throw e
        } catch (e: Exception) {
            // Offline or a server error: the cached hits stay; a later scroll or query retries.
            remotePages.update { if (it.mine()) it.copy(loading = false) else it }
            if (e is ApiException.Unauthorized) onUnauthorized()
        }
    }

    /** Tap: copy locally first, then `restore` in the background (online only). */
    fun copy(clip: ClipDto) {
        viewModelScope.launch {
            val api = remote()?.takeIf { isOnline() }
            when (clip.kind) {
                ClipKind.TEXT, ClipKind.LINK -> {
                    val text = clip.text ?: return@launch   // malformed clip: nothing to copy, no "Copied"
                    copier.writeText(text)
                }
                ClipKind.IMAGE -> {
                    // The local lookup may read from disk.
                    val png = withContext(io) { cachedImage(clip.id) } ?: api?.let { fetchImage(it, clip.id) }
                    if (png == null) {
                        _messages.trySend(UiText.Res(R.string.history_image_offline))
                        return@launch
                    }
                    try {
                        withContext(io) { copier.writeImage(clip.id, png) }
                    } catch (e: CancellationException) {
                        throw e
                    } catch (_: Exception) {
                        _messages.trySend(UiText.Res(R.string.copy_failed))
                        return@launch
                    }
                }
                ClipKind.UNKNOWN -> return@launch  // a kind from a newer client: nothing this build can copy
            }
            _messages.trySend(UiText.Res(R.string.copied))
            if (api != null) {
                launch(io) {
                    try {
                        api.restore(clip.id)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (_: ApiException.Unauthorized) {
                        onUnauthorized()
                    } catch (_: Exception) {
                        // Best effort: the local copy already happened.
                    }
                }
            }
        }
    }

    /** Confirmed long-press: delete on the server, then drop the row (the engine prunes the cache on the broadcast). */
    fun delete(clip: ClipDto) {
        viewModelScope.launch {
            val api = remote()
            if (api == null) {
                _messages.trySend(UiText.Res(R.string.history_delete_failed))
                return@launch
            }
            try {
                withContext(io) { api.delete(clip.id) }   // false means already gone: drop the row as well
                local.update { it.copy(deleted = it.deleted + clip.id) }
            } catch (e: CancellationException) {
                throw e
            } catch (_: ApiException.Unauthorized) {
                onUnauthorized()   // revoked: the app signs out; not a connection problem
            } catch (_: Exception) {
                _messages.trySend(UiText.Res(R.string.history_delete_failed))
            }
        }
    }

    /** Long-press menu: pin an unpinned clip, unpin a pinned one. The row changes at once; the broadcast follows. */
    fun togglePin(clip: ClipDto) {
        viewModelScope.launch {
            val api = remote()?.takeIf { isOnline() }
            if (api == null) {
                _messages.trySend(UiText.Res(R.string.history_pin_failed))
                return@launch
            }
            try {
                val changed = withContext(io) { api.pin(clip.id, pinned = clip.pinnedAt == null) }
                local.update { it.copy(updated = it.updated + (changed.id to changed)) }
            } catch (e: CancellationException) {
                throw e
            } catch (_: ApiException.Unauthorized) {
                onUnauthorized()
            } catch (e: ApiException.Rejected) {
                if (e.status == NOT_FOUND) {
                    local.update { it.copy(deleted = it.deleted + clip.id) }   // deleted on another device
                } else {
                    _messages.trySend(UiText.Res(R.string.history_pin_failed))
                }
            } catch (_: Exception) {
                _messages.trySend(UiText.Res(R.string.history_pin_failed))
            }
        }
    }

    /** On-device search: the clip's text contains [needle], ignoring case. Images and unknown clips have no text. */
    private fun ClipDto.matches(needle: String) = needle.isEmpty() || text?.contains(needle, ignoreCase = true) == true

    private fun isOnline() = syncState.value != SyncState.OFFLINE && syncState.value != SyncState.SIGNED_OUT

    private suspend fun fetchImage(api: ClipRemote, id: String): ByteArray? = try {
        withContext(io) { api.image(id) }
    } catch (e: CancellationException) {
        throw e
    } catch (_: ApiException.Unauthorized) {
        onUnauthorized()
        null
    } catch (_: Exception) {
        null
    }

    private companion object {
        const val PAGE_SIZE = 50
        const val OLDER_PAGE_SIZE = 200
        const val NOT_FOUND = 404
    }
}
