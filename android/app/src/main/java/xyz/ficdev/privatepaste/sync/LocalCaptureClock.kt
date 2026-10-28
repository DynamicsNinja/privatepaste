package xyz.ficdev.privatepaste.sync

import java.time.Instant
import java.util.concurrent.atomic.AtomicReference

/**
 * The time of the newest clip this phone sent (server time for a live upload, copy time when queued).
 * Catch-up never applies a remote clip older than this, so it can't overwrite something the owner just sent.
 */
class LocalCaptureClock {
    private val latest = AtomicReference<Instant?>(null)

    val lastLocalCaptureAt: Instant? get() = latest.get()

    /** Keeps the maximum; safe from any thread. */
    fun record(at: Instant) {
        latest.updateAndGet { current -> if (current == null || at.isAfter(current)) at else current }
    }
}
