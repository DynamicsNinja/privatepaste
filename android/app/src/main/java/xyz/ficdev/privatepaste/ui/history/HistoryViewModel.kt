package xyz.ficdev.privatepaste.ui.history

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import xyz.ficdev.privatepaste.R
import xyz.ficdev.privatepaste.api.PrivatePasteApi
import xyz.ficdev.privatepaste.clipboard.ClipboardWriter
import xyz.ficdev.privatepaste.core.model.ClipDto
import xyz.ficdev.privatepaste.core.model.ClipKind
import xyz.ficdev.privatepaste.core.model.ClipPage
import xyz.ficdev.privatepaste.sync.SyncState
import xyz.ficdev.privatepaste.ui.UiText
import kotlin.coroutines.CoroutineContext

/** The server calls the history screen makes. Production adapts [PrivatePasteApi]; tests pass a fake. */
interface ClipRemote {
    suspend fun clips(q: String?, cursor: String?, limit: Int): ClipPage
    suspend fun restore(id: String): ClipDto?
    suspend fun delete(id: String): Boolean
    suspend fun thumbnail(id: String): ByteArray
    suspend fun image(id: String): ByteArray
}

fun PrivatePasteApi.asClipRemote(): ClipRemote = object : ClipRemote {
    override suspend fun clips(q: String?, cursor: String?, limit: Int) = this@asClipRemote.clips(q, cursor, limit)
    override suspend fun restore(id: String) = this@asClipRemote.restore(id)
    override suspend fun delete(id: String) = this@asClipRemote.delete(id)
    override suspend fun thumbnail(id: String) = this@asClipRemote.thumbnail(id)
    override suspend fun image(id: String) = this@asClipRemote.image(id)
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

data class HistoryState(
    val query: String = "",
    val items: List<ClipDto> = emptyList(),
    /** The newest clip, when this phone applied it to its clipboard: it gets the live dot. */
    val liveClipId: String? = null,
    val syncState: SyncState = SyncState.CONNECTING,
    val loadingMore: Boolean = false,
)

/**
 * History list: the local cache shows at once; a query of 2+ characters also asks the server after a 300 ms
 * debounce, and the server results follow the cached hits, deduped by id. Older pages load with `nextCursor` when
 * the list nears its end. Taps copy locally first, then bump the clip on the server in the background.
 */
@OptIn(FlowPreview::class)
class HistoryViewModel(
    cached: StateFlow<List<ClipDto>>,
    private val lastAppliedHash: () -> String?,
    private val syncState: StateFlow<SyncState>,
    private val remote: () -> ClipRemote?,
    private val copier: ClipCopier,
    /** The full PNG of an image clip when this phone has it locally, else null. */
    private val cachedImage: (clipId: String) -> ByteArray? = { null },
    private val io: CoroutineContext = Dispatchers.IO,
) : ViewModel() {

    /** Server results for one normalized query; [loaded] once the first page arrived. */
    private data class Remote(
        val query: String,
        val items: List<ClipDto> = emptyList(),
        val next: String? = null,
        val loaded: Boolean = false,
        val loading: Boolean = false,
    )

    private val query = MutableStateFlow("")
    private val remotePages = MutableStateFlow(Remote(""))
    private val deleted = MutableStateFlow<Set<String>>(emptySet())
    private var pageJob: Job? = null

    private val _messages = Channel<UiText>(Channel.BUFFERED)

    /** One-shot toasts ("Copied", "Image not available offline", ...). */
    val messages: Flow<UiText> = _messages.receiveAsFlow()

    val state: StateFlow<HistoryState> =
        combine(cached, query, remotePages, deleted, syncState) { cache, q, page, gone, sync ->
            val needle = q.trim()
            val hits = if (needle.isEmpty()) cache else cache.filter { it.text?.contains(needle, ignoreCase = true) == true }
            val fromServer = if (page.query == needle) page.items else emptyList()
            val ids = hits.mapTo(HashSet()) { it.id }
            val items = (hits + fromServer.filter { ids.add(it.id) }).filter { it.id !in gone }
            val newest = cache.firstOrNull()
            HistoryState(
                query = q,
                items = items,
                liveClipId = newest?.takeIf { it.id !in gone && it.hash == lastAppliedHash() }?.id,
                syncState = sync,
                loadingMore = page.query == needle && page.loading,
            )
        }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), HistoryState(syncState = syncState.value))

    init {
        viewModelScope.launch {
            query.debounce(SEARCH_DEBOUNCE_MS).collectLatest { raw ->
                val q = raw.trim()
                val page = remotePages.value
                if (q.length >= MIN_SERVER_QUERY && page.query == q && !page.loaded && !page.loading) load(q, null)
            }
        }
    }

    fun onQueryChange(value: String) {
        val old = query.value.trim()
        query.value = value
        if (value.trim() != old) {
            pageJob?.cancel()
            remotePages.value = Remote(value.trim())
        }
    }

    /** The list is near its end: load the next server page. */
    fun onNearEnd() {
        val q = query.value.trim()
        val page = remotePages.value
        if (page.query != q || page.loading) return
        if (q.isNotEmpty() && (q.length < MIN_SERVER_QUERY || !page.loaded)) return   // the debounce loads page 1
        if (page.loaded && page.next == null) return
        pageJob = viewModelScope.launch { load(q, page.next) }
    }

    private suspend fun load(q: String, cursor: String?) {
        val api = remote() ?: return
        remotePages.update { if (it.query == q) it.copy(loading = true) else it }
        try {
            val result = withContext(io) { api.clips(q.ifEmpty { null }, cursor, PAGE_SIZE) }
            remotePages.update {
                if (it.query != q) it
                else it.copy(items = it.items + result.items, next = result.nextCursor, loaded = true, loading = false)
            }
        } catch (e: CancellationException) {
            remotePages.update { if (it.query == q) it.copy(loading = false) else it }
            throw e
        } catch (_: Exception) {
            // Offline or a server error: the cached hits stay; a later scroll or query retries.
            remotePages.update { if (it.query == q) it.copy(loading = false) else it }
        }
    }

    /** Tap: copy locally first, then `restore` in the background (online only). */
    fun copy(clip: ClipDto) {
        viewModelScope.launch {
            val api = remote()?.takeIf { isOnline() }
            when (clip.kind) {
                ClipKind.TEXT, ClipKind.LINK -> copier.writeText(clip.text.orEmpty())
                ClipKind.IMAGE -> {
                    val png = cachedImage(clip.id) ?: api?.let { fetchImage(it, clip.id) }
                    if (png == null) {
                        _messages.trySend(UiText.Res(R.string.history_image_offline))
                        return@launch
                    }
                    try {
                        withContext(io) { copier.writeImage(clip.id, png) }
                    } catch (e: CancellationException) {
                        throw e
                    } catch (_: Exception) {
                        _messages.trySend(UiText.Res(R.string.history_image_offline))
                        return@launch
                    }
                }
            }
            _messages.trySend(UiText.Res(R.string.history_copied))
            if (api != null) {
                launch(io) {
                    try {
                        api.restore(clip.id)
                    } catch (e: CancellationException) {
                        throw e
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
                deleted.update { it + clip.id }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                _messages.trySend(UiText.Res(R.string.history_delete_failed))
            }
        }
    }

    private fun isOnline() = syncState.value != SyncState.OFFLINE && syncState.value != SyncState.SIGNED_OUT

    private suspend fun fetchImage(api: ClipRemote, id: String): ByteArray? = try {
        withContext(io) { api.image(id) }
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        null
    }

    private companion object {
        const val SEARCH_DEBOUNCE_MS = 300L
        const val MIN_SERVER_QUERY = 2
        const val PAGE_SIZE = 50
    }
}
