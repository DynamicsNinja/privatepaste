package xyz.ficdev.privatepaste.ui

import java.time.Clock
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * Row timestamps: "just now" (under a minute, also for a server clock slightly ahead), "{n} min", "{n} h" (under
 * 24 hours), "yesterday" (the previous calendar day in [zone]), then "d MMM". [clock] and [zone] are injected so
 * tests are deterministic.
 */
class RelativeTime(
    private val clock: Clock = Clock.systemDefaultZone(),
    private val zone: ZoneId = clock.zone,
    locale: Locale = Locale.getDefault(),
) {
    private val dayMonth = DateTimeFormatter.ofPattern("d MMM", locale)

    fun format(at: Instant): String {
        val now = clock.instant()
        val elapsed = Duration.between(at, now)
        return when {
            elapsed < Duration.ofMinutes(1) -> "just now"
            elapsed < Duration.ofHours(1) -> "${elapsed.toMinutes()} min"
            elapsed < Duration.ofDays(1) -> "${elapsed.toHours()} h"
            at.atZone(zone).toLocalDate() == now.atZone(zone).toLocalDate().minusDays(1) -> "yesterday"
            else -> dayMonth.format(at.atZone(zone))
        }
    }
}
