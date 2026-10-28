package xyz.ficdev.privatepaste.sync

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.random.Random
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

class BackoffTest {

    /** A Random whose nextDouble is always [value]. */
    private class FixedRandom(private val value: Double) : Random() {
        override fun nextBits(bitCount: Int): Int = 0
        override fun nextDouble(): Double = value
    }

    private val expectedBases = listOf(1, 2, 4, 8, 16, 30, 30, 30).map { it.seconds }

    @Test
    fun doublesFromOneSecondToThirtySecondCap() {
        val backoff = Backoff(FixedRandom(0.5))   // factor exactly 1
        assertEquals(expectedBases, List(expectedBases.size) { backoff.next() })
    }

    @Test
    fun jitterStaysWithinTwentyPercent() {
        val low = Backoff(FixedRandom(0.0))
        val high = Backoff(FixedRandom(0.999999))
        for (base in expectedBases) {
            assertEquals(base * 0.8, low.next())
            val h = high.next()
            assertTrue("$h for $base", h <= base * 1.2 && h > base * 1.19)
        }
        val seeded = Backoff(Random(42))
        repeat(200) { i ->
            val base = expectedBases.getOrElse(i) { 30.seconds }
            val d = seeded.next()
            assertTrue("$d for $base", d >= base * 0.8 - 1.milliseconds && d <= base * 1.2 + 1.milliseconds)
        }
    }

    @Test
    fun resetStartsOverAtOneSecond() {
        val backoff = Backoff(FixedRandom(0.5))
        repeat(4) { backoff.next() }
        backoff.reset()
        assertEquals(listOf<Duration>(1.seconds, 2.seconds), List(2) { backoff.next() })
    }
}
