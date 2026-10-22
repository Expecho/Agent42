package nl.agent42.android.chat

import kotlinx.serialization.Serializable

/** `ToolDescriptor` (GET /api/tools, ADR 0021): the selectable-tools catalog. */
@Serializable
data class ToolDescriptor(
    val key: String,
    val label: String,
    val requiresHostedTools: Boolean = false,
    val parentKey: String? = null,
    val description: String? = null,
)

/**
 * The per-conversation tool picker, as a pure model of the web's (wwwroot/js/tools.js renderToolChecks /
 * readToolChecks), so a save from the app stores exactly what the web would. The rules, each load-bearing there:
 *  - A row is checked when its key is selected, OR a bare `mcp` / `skills` wildcard covers it, OR (an MCP tool) its
 *    server's key is selected. A server row is checked when that key is selected or ALL its tools are.
 *  - COLLAPSE: a fully checked server saves as the bare `mcp:{id}` (keeps ADR 0026's "all of this server's tools,
 *    including ones it adds later"); a partly checked one saves its tool keys, never the server key.
 *  - PRESERVE: selected tool keys of a listed server that the catalog has no row for are carried forward; keys of a
 *    server that is gone are dropped (they self-heal, as ToolCatalog.Sanitize promises).
 */
class ToolPicker(private val catalog: List<ToolDescriptor>, selected: List<String>) {
    val builtins = catalog.filter { !it.key.startsWith(MCP) && !it.key.startsWith(SKILL) }
    val skills = catalog.filter { it.key.startsWith(SKILL) }
    val servers = catalog.filter { it.key.startsWith(MCP) && it.parentKey == null }

    fun toolsOf(server: ToolDescriptor) = catalog.filter { it.parentKey == server.key }

    private val checked = mutableSetOf<String>()
    private val preserved: List<String>

    init {
        val selectedSet = selected.toSet()
        val mcpWildcard = "mcp" in selectedSet
        val skillsWildcard = "skills" in selectedSet
        catalog.filter { tool ->
            tool.key in selectedSet ||
                (mcpWildcard && tool.key.startsWith(MCP)) ||
                (skillsWildcard && tool.key.startsWith(SKILL)) ||
                (tool.parentKey != null && tool.parentKey in selectedSet)
        }.forEach { checked += it.key }
        // A server whose tools are all selected is fully enabled however the selection spells it (the grain expands
        // `mcp:{id}` into per-tool keys on read).
        servers.forEach { server ->
            val tools = toolsOf(server)
            if (tools.isNotEmpty() && tools.all { it.key in checked }) checked += server.key
        }
        val rendered = catalog.map { it.key }.toSet()
        val serverKeys = servers.map { it.key }
        preserved = selected.filter { k -> k !in rendered && serverKeys.any { k.startsWith("$it:") } }
    }

    fun isChecked(key: String) = key in checked

    /** Toggles a row. A server row sets all its tools; a tool row re-derives its server from its siblings. */
    fun toggle(key: String) {
        val on = key !in checked
        val tool = catalog.firstOrNull { it.key == key } ?: return
        set(key, on)
        val server = servers.firstOrNull { it.key == key }
        if (server != null) {
            toolsOf(server).forEach { set(it.key, on) }
        } else {
            tool.parentKey?.let { parent ->
                val siblings = catalog.filter { it.parentKey == parent }
                set(parent, siblings.all { it.key in checked })
            }
        }
    }

    /** The keys to PUT, in catalog order, with the collapse and preserve rules applied. */
    fun selection(): List<String> {
        val collapsed = servers.filter { it.key in checked }.map { it.key }
        fun collapse(keys: List<String>) = keys.filter { k -> collapsed.none { k.startsWith("$it:") } }
        return collapse(catalog.map { it.key }.filter { it in checked }) + collapse(preserved)
    }

    /** "3/7": checked top-level rows (built-ins, skills, servers) out of all, as the web's badge counts them. */
    fun count(): Pair<Int, Int> {
        val top = builtins + skills + servers
        return top.count { it.key in checked } to top.size
    }

    private fun set(key: String, on: Boolean) {
        if (on) checked += key else checked -= key
    }

    private companion object {
        const val MCP = "mcp:"
        const val SKILL = "skill:"
    }
}
