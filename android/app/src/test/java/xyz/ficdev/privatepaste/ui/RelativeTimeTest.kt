package xyz.ficdev.privatepaste.ui

import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.Clock
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

class RelativeTimeTest {

    private val zone = ZoneId.of("Europe/Zagreb")
    private val now = Instant.parse("2026-10-28T14:30:00Z")   // 15:30 local
    private val time = RelativeTime(Clock.fixed(now, zone), zone, Locale.ENGLISH)

    private fun ago(seconds: Long) = time.format(now.minusSeconds(seconds))

    @Test
    fun underAMinuteIsJustNow() {
        assertEquals("just now", ago(0))
        assertEquals("just now", ago(59))
    }

    @Test
    fun futureTimestampsAreJustNow() {
        assertEquals("just now", time.format(now.plusSeconds(120)))
    }

    @Test
    fun minutesUnderAnHour() {
        assertEquals("1 min", ago(60))
        assertEquals("59 min", ago(59 * 60 + 59))
    }

    @Test
    fun hoursUnderADay() {
        assertEquals("1 h", ago(3600))
        assertEquals("15 h", ago(15 * 3600 + 20 * 60))   // 00:10 local today
        assertEquals("23 h", ago(23 * 3600 + 59 * 60))   // yesterday's date, but under 24 h
    }

    @Test
    fun previousCalendarDayAfter24HoursIsYesterday() {
        assertEquals("yesterday", ago(24 * 3600))
        assertEquals("yesterday", ago(39 * 3600))         // 00:30 local, Oct 27
    }

    @Test
    fun olderIsDayAndMonth() {
        assertEquals("26 Oct", ago(40 * 3600))            // 23:30 local, Oct 26
        assertEquals("3 Jan", time.format(Instant.parse("2026-01-03T09:00:00Z")))
    }

    @Test
    fun usesTheInjectedZone() {
        // 23:30 UTC on Oct 26 is 00:30 on Oct 27 in Zagreb (yesterday) but Oct 26 in UTC.
        val then = Instant.parse("2026-10-26T23:30:00Z")
        assertEquals("yesterday", time.format(then))
        assertEquals("26 Oct", RelativeTime(Clock.fixed(now, ZoneId.of("UTC")), ZoneId.of("UTC"), Locale.ENGLISH).format(then))
    }
}
