package xyz.ficdev.privatepaste.send

import org.junit.Assert.assertEquals
import org.junit.Test

class SharedTextTest {

    @Test
    fun chromeSelectionShareKeepsOnlyTheSelection() {
        val shared = "\"Požar u Omišu: 76 dana, 6 mrtvih, 0 upozorenja. HDZ-u je to \"briljantno\"\"\n " +
            "https://www.index.hr/vijesti/clanak/pozar-u-omisu/2850885.aspx#:~:text=Po%C5%BEar%20u,-Omi%C5%A1u"
        assertEquals(
            "Požar u Omišu: 76 dana, 6 mrtvih, 0 upozorenja. HDZ-u je to \"briljantno\"",
            SharedText.selectionOnly(shared),
        )
    }

    @Test
    fun multiLineSelectionAndCurlyQuotesAreHandled() {
        val shared = "“first line\nsecond line”\nhttps://example.com/a#:~:text=first%20line"
        assertEquals("first line\nsecond line", SharedText.selectionOnly(shared))
    }

    @Test
    fun ordinaryTextAndLinksAreUnchanged() {
        listOf(
            "just some text",
            "https://example.com/page",
            "\"quoted\" but no link",
            "\"quoted\"\nhttps://example.com/no-text-fragment",
            "Look at this: https://example.com/a#:~:text=x",
        ).forEach { assertEquals(it, SharedText.selectionOnly(it)) }
    }
}
