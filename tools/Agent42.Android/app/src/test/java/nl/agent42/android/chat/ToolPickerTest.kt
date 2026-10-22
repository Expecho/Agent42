package nl.agent42.android.chat

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ToolPickerTest {
    private val catalog = listOf(
        ToolDescriptor("web_search", "Web search"),
        ToolDescriptor("location", "Location"),
        ToolDescriptor("skill:pdf", "pdf"),
        ToolDescriptor("mcp:cal", "Calendar"),
        ToolDescriptor("mcp:cal:list", "list", parentKey = "mcp:cal"),
        ToolDescriptor("mcp:cal:create", "create", parentKey = "mcp:cal"),
        ToolDescriptor("mcp:gh", "GitHub"),
    )

    @Test
    fun wildcardsCheckTheirWholeGroup() {
        val picker = ToolPicker(catalog, listOf("web_search", "mcp", "skills"))

        assertTrue(picker.isChecked("skill:pdf"))
        assertTrue(picker.isChecked("mcp:cal"))
        assertTrue(picker.isChecked("mcp:cal:create"))
        assertFalse(picker.isChecked("location"))
        // Saving re-emits explicit keys instead of dropping the wildcard (the ADR 0026 hazard), and collapses servers.
        assertEquals(listOf("web_search", "skill:pdf", "mcp:cal", "mcp:gh"), picker.selection())
    }

    @Test
    fun aServerIsCheckedWhenAllItsToolsAre_andThenSavesAsTheServerKey() {
        // The grain's read expands mcp:cal into its per-tool keys.
        val picker = ToolPicker(catalog, listOf("mcp:cal:list", "mcp:cal:create"))

        assertTrue(picker.isChecked("mcp:cal"))
        assertEquals(listOf("mcp:cal"), picker.selection())
    }

    @Test
    fun unTickingOneToolSavesThePartialSet_neverTheServerKey() {
        val picker = ToolPicker(catalog, listOf("mcp:cal"))

        picker.toggle("mcp:cal:create")

        assertFalse(picker.isChecked("mcp:cal"))
        assertEquals(listOf("mcp:cal:list"), picker.selection())
    }

    @Test
    fun tickingTheServerTicksItsTools() {
        val picker = ToolPicker(catalog, emptyList())

        picker.toggle("mcp:cal")

        assertTrue(picker.isChecked("mcp:cal:list"))
        assertEquals(listOf("mcp:cal"), picker.selection())
    }

    @Test
    fun unknownToolsOfAListedServerArePreserved_aGoneServersAreDropped() {
        val picker = ToolPicker(catalog, listOf("mcp:cal:list", "mcp:gh:search", "mcp:deleted:x"))

        assertEquals(listOf("mcp:cal:list", "mcp:gh:search"), picker.selection())
    }

    @Test
    fun theCountIsTopLevelRowsOnly() {
        val picker = ToolPicker(catalog, listOf("web_search", "mcp:cal"))

        assertEquals(2 to 5, picker.count())
    }
}
