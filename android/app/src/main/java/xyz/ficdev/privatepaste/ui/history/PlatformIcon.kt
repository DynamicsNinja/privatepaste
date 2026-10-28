package xyz.ficdev.privatepaste.ui.history

import androidx.annotation.DrawableRes
import androidx.annotation.StringRes
import xyz.ficdev.privatepaste.R

/** The icon shown before a clip's source device name, drawn from branding/platform-icons. */
enum class PlatformIcon(@param:DrawableRes val drawable: Int, @param:StringRes val label: Int) {
    WINDOWS(R.drawable.ic_platform_windows, R.string.platform_windows),
    MACOS(R.drawable.ic_platform_macos, R.string.platform_macos),
    LINUX(R.drawable.ic_platform_linux, R.string.platform_linux),
    ANDROID(R.drawable.ic_platform_android, R.string.platform_android),
    WEB(R.drawable.ic_platform_web, R.string.platform_web),
    UNKNOWN(R.drawable.ic_platform_unknown, R.string.platform_unknown),
    ;

    companion object {
        /** Maps a clip's `sourceDevicePlatform`; anything unrecognized (or null) is [UNKNOWN]. */
        fun forPlatform(platform: String?): PlatformIcon = when (platform?.trim()?.lowercase()) {
            "windows" -> WINDOWS
            "macos" -> MACOS
            "linux" -> LINUX
            "android" -> ANDROID
            "web" -> WEB
            else -> UNKNOWN
        }
    }
}
