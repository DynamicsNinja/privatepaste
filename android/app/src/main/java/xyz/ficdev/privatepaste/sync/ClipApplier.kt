package xyz.ficdev.privatepaste.sync

import xyz.ficdev.privatepaste.core.model.ClipDto

/**
 * Puts a clip from another device on this phone's clipboard (or posts its Copy notification).
 * Returns true when it was written or the notification was posted; only then does the engine record it as applied.
 */
fun interface ClipApplier {
    suspend fun apply(clip: ClipDto): Boolean
}
