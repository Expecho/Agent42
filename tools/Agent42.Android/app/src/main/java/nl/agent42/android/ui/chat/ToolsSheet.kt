package nl.agent42.android.ui.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import nl.agent42.android.chat.ToolDescriptor
import nl.agent42.android.chat.ToolPicker
import nl.agent42.android.theme.Agent42
import nl.agent42.android.ui.MetaLabel

/**
 * The per-chat tool picker (ADR 0021), as the web's composer popover: Built-in, Skills, and MCP servers with their
 * tools folded under them. Every toggle saves at once. Agent chats don't get it: their tools come from the agent.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ToolsSheet(vm: ChatViewModel) {
    if (!vm.toolsOpen) return
    val c = Agent42.colors
    @Suppress("UNUSED_VARIABLE") val version = vm.toolsVersion // read, so every toggle recomposes the sheet
    ModalBottomSheet(
        onDismissRequest = vm::closeTools,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = c.bgRaised,
    ) {
        val picker = vm.tools
        if (picker == null) {
            Text("TOOLS", style = MaterialTheme.typography.titleMedium, color = c.amber, modifier = Modifier.padding(horizontal = 20.dp))
            Text("loading your tools…", style = MaterialTheme.typography.bodySmall, color = c.inkDim, modifier = Modifier.padding(20.dp))
            return@ModalBottomSheet
        }
        val (on, total) = picker.count()
        Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("TOOLS", style = MaterialTheme.typography.titleMedium, color = c.amber, modifier = Modifier.weight(1f))
            Text("$on/$total on", style = MaterialTheme.typography.labelSmall, color = c.inkFaint)
        }
        Text(
            "For this chat only. A new tool starts off once you've saved a selection.",
            style = MaterialTheme.typography.bodySmall,
            color = c.inkDim,
            modifier = Modifier.padding(horizontal = 20.dp, vertical = 8.dp),
        )
        LazyColumn(Modifier.fillMaxWidth().padding(bottom = 24.dp)) {
            group("BUILT-IN", picker.builtins, picker, vm)
            group("SKILLS", picker.skills, picker, vm)
            if (picker.servers.isNotEmpty()) {
                item { MetaLabel("MCP SERVERS", Modifier.padding(start = 20.dp, top = 18.dp, bottom = 6.dp)) }
                items(picker.servers, key = { it.key }) { server -> ServerRow(server, picker, vm) }
            }
        }
    }
}

private fun LazyListScope.group(heading: String, tools: List<ToolDescriptor>, picker: ToolPicker, vm: ChatViewModel) {
    if (tools.isEmpty()) return
    item { MetaLabel(heading, Modifier.padding(start = 20.dp, top = 18.dp, bottom = 6.dp)) }
    items(tools, key = { it.key }) { ToolRow(it, picker.isChecked(it.key)) { vm.toggleTool(it.key) } }
}

@Composable
private fun ServerRow(server: ToolDescriptor, picker: ToolPicker, vm: ChatViewModel) {
    var open by remember { mutableStateOf(false) }
    val tools = picker.toolsOf(server)
    val c = Agent42.colors
    Column {
        ToolRow(server, picker.isChecked(server.key), trailing = if (tools.isEmpty()) null else "${if (open) "▾" else "▸"} ${tools.size}", onTrailing = { open = !open }) {
            vm.toggleTool(server.key)
        }
        if (open) {
            Column(Modifier.padding(start = 28.dp)) {
                tools.forEach { ToolRow(it, picker.isChecked(it.key), dim = true) { vm.toggleTool(it.key) } }
            }
            Box(Modifier.padding(start = 48.dp).fillMaxWidth().background(c.lineSoft).size(1.dp))
        }
    }
}

/** One checkbox row in the web's style: a small amber-filled square, the label, the server's one-line description. */
@Composable
private fun ToolRow(
    tool: ToolDescriptor,
    checked: Boolean,
    dim: Boolean = false,
    trailing: String? = null,
    onTrailing: () -> Unit = {},
    onToggle: () -> Unit,
) {
    val c = Agent42.colors
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onToggle).padding(horizontal = 20.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Box(
            Modifier
                .size(18.dp)
                .background(if (checked) c.amber else c.bgInset, RoundedCornerShape(4.dp))
                .border(1.dp, if (checked) c.amber else c.line, RoundedCornerShape(4.dp)),
            contentAlignment = Alignment.Center,
        ) {
            if (checked) Text("✓", style = MaterialTheme.typography.labelSmall, color = c.bg)
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                if (tool.requiresHostedTools) "${tool.label} (Responses)" else tool.label,
                style = MaterialTheme.typography.bodyMedium,
                color = if (dim) c.inkDim else c.ink,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            tool.description?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = c.inkFaint) }
        }
        trailing?.let {
            Text(it, style = MaterialTheme.typography.labelSmall, color = c.inkDim, modifier = Modifier.clickable(onClick = onTrailing).padding(8.dp))
        }
    }
}
