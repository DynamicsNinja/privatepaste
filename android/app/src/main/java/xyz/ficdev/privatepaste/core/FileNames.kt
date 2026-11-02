package xyz.ficdev.privatepaste.core

import xyz.ficdev.privatepaste.core.model.ClipFileInfo

/**
 * File name rules shared by every client (port of FileNames.cs). A name in a payload comes from another device and is
 * untrusted: [safe] turns it into a leaf name that can't leave the folder it is written to. The vectors in
 * docs/e2e-test-vectors.json ("safeFileName", "fileSize", "fileRowText") pin these rules on every platform.
 */
object FileNames {
    private const val MAX_LENGTH = 200
    private const val FALLBACK = "file"
    private const val OCTET = "application/octet-stream"
    private const val FORBIDDEN = "<>:\"|?*"

    private val mimeByExtension = mapOf(
        "pdf" to "application/pdf",
        "txt" to "text/plain",
        "csv" to "text/csv",
        "json" to "application/json",
        "zip" to "application/zip",
        "png" to "image/png",
        "jpg" to "image/jpeg",
        "jpeg" to "image/jpeg",
        "gif" to "image/gif",
        "webp" to "image/webp",
        "svg" to "image/svg+xml",
        "mp4" to "video/mp4",
        "mp3" to "audio/mpeg",
        "doc" to "application/msword",
        "docx" to "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xls" to "application/vnd.ms-excel",
        "xlsx" to "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "ppt" to "application/vnd.ms-powerpoint",
        "pptx" to "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    )

    /**
     * The part after the last / or \; control characters and `<>:"|?*` dropped; trailing dots and spaces trimmed;
     * "file" when nothing usable is left; a Windows reserved device name (CON, NUL, COM1, ...) prefixed with _; cut to
     * 200 UTF-16 units keeping the extension (never splitting a surrogate pair, the stem re-trimmed).
     */
    fun safe(name: String): String {
        val leaf = name.substring(name.lastIndexOfAny(charArrayOf('/', '\\')) + 1)
        val sb = StringBuilder(leaf.length)
        for (c in leaf) if (c.code >= 0x20 && c.code != 0x7F && FORBIDDEN.indexOf(c) < 0) sb.append(c)
        var s = sb.toString().trimEnd('.', ' ')
        if (s == "" || s == "." || s == "..") return FALLBACK

        val dot = s.indexOf('.')
        if (isReserved(if (dot < 0) s else s.substring(0, dot))) s = "_$s"

        if (s.length > MAX_LENGTH) {
            var ext = extensionOf(s)
            if (ext.length >= MAX_LENGTH) ext = ""
            var keep = MAX_LENGTH - ext.length
            if (Character.isHighSurrogate(s[keep - 1]) && Character.isLowSurrogate(s[keep])) keep--
            val stem = s.substring(0, keep).trimEnd('.', ' ')
            s = (stem.ifEmpty { FALLBACK }) + ext
        }
        return s
    }

    /** [safeName] if nothing by that name is [taken], else "name (2).ext", "name (3).ext", ... */
    fun unique(safeName: String, taken: (String) -> Boolean): String {
        if (!taken(safeName)) return safeName
        val ext = extensionOf(safeName)
        val stem = safeName.substring(0, safeName.length - ext.length)
        var n = 2
        while (true) {
            val candidate = "$stem ($n)$ext"
            if (!taken(candidate)) return candidate
            n++
        }
    }

    /** A MIME type from the extension, for labelling only; application/octet-stream when unknown. */
    fun mimeFor(name: String): String {
        val ext = extensionOf(name)
        return if (ext.length > 1) mimeByExtension[ext.substring(1).lowercase()] ?: OCTET else OCTET
    }

    /** "0 B", "1023 B", "1.5 KB", "12 KB", "3.4 MB": the same integer arithmetic as FileNames.FormatSize. */
    fun formatSize(bytes: Long): String {
        if (bytes < 1024) return "$bytes B"
        val (text, kbOverflow) = scaled(bytes, 1024, "KB")
        return if (kbOverflow) scaled(bytes, 1024L * 1024, "MB").first else text
    }

    private fun scaled(bytes: Long, unit: Long, name: String): Pair<String, Boolean> {
        val tenths = (bytes * 10 + unit / 2) / unit
        if (tenths < 100) {
            return (if (tenths % 10 == 0L) "${tenths / 10} $name" else "${tenths / 10}.${tenths % 10} $name") to false
        }
        val whole = (bytes + unit / 2) / unit
        return "$whole $name" to (name == "KB" && whole >= 1024)
    }

    /** The row text of a file clip: "report.pdf +2 more · 3.4 MB". */
    fun rowText(files: List<ClipFileInfo>): String {
        if (files.isEmpty()) return ""
        val more = if (files.size > 1) " +${files.size - 1} more" else ""
        return "${files[0].name}$more · ${formatSize(files.sumOf { it.size })}"
    }

    // Starts at the last dot unless the dot starts the name (".bashrc" has no extension); "" when there is none.
    private fun extensionOf(name: String): String {
        val dot = name.lastIndexOf('.')
        return if (dot > 0) name.substring(dot) else ""
    }

    private fun isReserved(stem: String): Boolean {
        if (stem.length == 3) return stem.uppercase() in setOf("CON", "PRN", "AUX", "NUL")
        return stem.length == 4 && stem[3] in '1'..'9' &&
            (stem.startsWith("COM", ignoreCase = true) || stem.startsWith("LPT", ignoreCase = true))
    }
}
