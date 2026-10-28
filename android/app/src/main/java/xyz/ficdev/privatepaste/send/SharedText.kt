package xyz.ficdev.privatepaste.send

/** Clean-up of text that other apps put in a share intent. */
object SharedText {

    /**
     * Chrome shares a text selection as `"<selection>"` + newline + a link to the page with a `#:~:text=` fragment that
     * highlights it. The owner shares a selection to copy the selection, so keep only that. Anything else is unchanged.
     */
    private val chromeSelection =
        Regex("""^\s*["“]([\s\S]+)["”]\s*\n\s*https?://\S+#:~:text=\S*\s*$""")

    fun selectionOnly(text: String): String =
        chromeSelection.matchEntire(text)?.groupValues?.get(1) ?: text
}
