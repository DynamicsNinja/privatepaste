package xyz.ficdev.privatepaste.sync

import kotlin.random.Random
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * Reconnect delays: 1 s doubling to a 30 s cap, each with ±20% jitter from [random].
 * Not thread-safe; the sync engine uses it from its single loop.
 */
class Backoff(private val random: Random = Random.Default) {
    private var current: Duration = INITIAL

    fun next(): Duration {
        val base = current
        current = minOf(current * 2, CAP)
        val factor = 1 + (random.nextDouble() * 2 - 1) * JITTER
        return base * factor
    }

    fun reset() {
        current = INITIAL
    }

    private companion object {
        val INITIAL = 1.seconds
        val CAP = 30.seconds
        const val JITTER = 0.2
    }
}
