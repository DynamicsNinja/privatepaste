package xyz.ficdev.privatepaste.ui

import org.junit.Assert.assertEquals
import org.junit.Test
import xyz.ficdev.privatepaste.ui.history.PlatformIcon
import java.io.File

class PlatformIconTest {

    @Test
    fun mapsPlatformsToIcons() {
        assertEquals(PlatformIcon.WINDOWS, PlatformIcon.forPlatform("windows"))
        assertEquals(PlatformIcon.MACOS, PlatformIcon.forPlatform("macos"))
        assertEquals(PlatformIcon.LINUX, PlatformIcon.forPlatform("linux"))
        assertEquals(PlatformIcon.ANDROID, PlatformIcon.forPlatform("android"))
        assertEquals(PlatformIcon.WEB, PlatformIcon.forPlatform("web"))
        assertEquals(PlatformIcon.ANDROID, PlatformIcon.forPlatform(" Android "))
        assertEquals(PlatformIcon.UNKNOWN, PlatformIcon.forPlatform("unknown"))
        assertEquals(PlatformIcon.UNKNOWN, PlatformIcon.forPlatform("ios"))
        assertEquals(PlatformIcon.UNKNOWN, PlatformIcon.forPlatform(""))
        assertEquals(PlatformIcon.UNKNOWN, PlatformIcon.forPlatform(null))
    }

    /** The VectorDrawables must carry the same path data as branding/platform-icons (and ui-icons/pin.svg). */
    @Test
    fun drawablesMatchTheBrandingSvgs() {
        val repo = generateSequence(File("").absoluteFile) { it.parentFile }
            .first { File(it, "branding/platform-icons").isDirectory }
        val pathData = Regex("android:pathData=\"([^\"]+)\"")
        val svgPath = Regex("<path d=\"([^\"]+)\"")
        for (icon in PlatformIcon.entries) {
            val key = icon.name.lowercase()
            val vector = File(repo, "android/app/src/main/res/drawable/ic_platform_$key.xml").readText()
            val svg = File(repo, "branding/platform-icons/$key.svg").readText()
            assertEquals(key, svgPath.find(svg)!!.groupValues[1], pathData.find(vector)!!.groupValues[1])
        }
        val pinVector = File(repo, "android/app/src/main/res/drawable/ic_pin.xml").readText()
        val pinSvg = File(repo, "branding/ui-icons/pin.svg").readText()
        assertEquals("pin", svgPath.find(pinSvg)!!.groupValues[1], pathData.find(pinVector)!!.groupValues[1])
    }
}
