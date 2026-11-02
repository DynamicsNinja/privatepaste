package xyz.ficdev.privatepaste.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import xyz.ficdev.privatepaste.core.model.ClipFileInfo
import xyz.ficdev.privatepaste.core.model.ProtocolJson
import java.io.File

class FileNamesTest {
    private val vectors = Json.parseToJsonElement(File("../../docs/e2e-test-vectors.json").readText(Charsets.UTF_8)).jsonObject

    @Test
    fun safeFileNameVectors() {
        val cases = vectors.getValue("safeFileName").jsonArray
        assertTrue(cases.size >= 10)
        for (case in cases) {
            val input = case.jsonObject.getValue("in").jsonPrimitive.content
            val expected = case.jsonObject.getValue("out").jsonPrimitive.content
            assertEquals("safe(${input.take(40)})", expected, FileNames.safe(input))
        }
    }

    @Test
    fun fileSizeVectors() {
        for (case in vectors.getValue("fileSize").jsonArray) {
            val bytes = case.jsonObject.getValue("bytes").jsonPrimitive.long
            assertEquals("formatSize($bytes)", case.jsonObject.getValue("text").jsonPrimitive.content, FileNames.formatSize(bytes))
        }
    }

    @Test
    fun fileRowTextVectors() {
        for (case in vectors.getValue("fileRowText").jsonArray) {
            val files = ProtocolJson.json.decodeFromJsonElement(
                kotlinx.serialization.builtins.ListSerializer(ClipFileInfo.serializer()), case.jsonObject.getValue("files"),
            )
            assertEquals(case.jsonObject.getValue("text").jsonPrimitive.content, FileNames.rowText(files))
        }
    }

    @Test
    fun uniqueCountsBeforeTheExtension() {
        assertEquals("report.pdf", FileNames.unique("report.pdf") { false })
        assertEquals("report (2).pdf", FileNames.unique("report.pdf") { it == "report.pdf" })
        assertEquals("report (3).pdf", FileNames.unique("report.pdf") { it in setOf("report.pdf", "report (2).pdf") })
        assertEquals("README (2)", FileNames.unique("README") { it == "README" })
    }

    @Test
    fun mimeComesFromTheSharedTable() {
        assertEquals("application/pdf", FileNames.mimeFor("x.PDF"))
        assertEquals("image/jpeg", FileNames.mimeFor("a.jpeg"))
        assertEquals("application/octet-stream", FileNames.mimeFor("a.exe"))
        assertEquals("application/octet-stream", FileNames.mimeFor(".pdf"))
        assertEquals("application/octet-stream", FileNames.mimeFor("noext"))
    }
}
