package nl.agent42.android.ui.chat

import android.content.Intent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SuggestionChip
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import androidx.lifecycle.viewmodel.compose.viewModel
import com.mikepenz.markdown.m3.Markdown
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.chat.Role
import nl.agent42.android.chat.UiTurn

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(container: AppContainer, chat: Screen.Chat) {
    val vm = viewModel(key = "chat-${chat.id}") { ChatViewModel(container, chat) }
    val context = LocalContext.current
    val list = rememberLazyListState()
    LaunchedEffect(vm.turns.size, vm.turns.lastOrNull()?.text?.length) {
        if (vm.turns.isNotEmpty()) list.animateScrollToItem(vm.turns.lastIndex)
    }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(chat.title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                actions = {
                    if (!chat.agentBound) ModelPicker(vm)
                    // The web app has no per-conversation link, so this opens its home page (ADR 0114: rich content
                    // such as artifacts and mermaid lives there).
                    TextButton(onClick = {
                        container.session?.baseUrl?.let { context.startActivity(Intent(Intent.ACTION_VIEW, it.toUri())) }
                    }) { Text("Web") }
                },
            )
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize().imePadding()) {
            LazyColumn(state = list, modifier = Modifier.weight(1f).fillMaxWidth()) {
                itemsIndexed(vm.turns) { index, turn ->
                    Bubble(turn, isLast = index == vm.turns.lastIndex, onSuggestion = vm::send)
                }
            }
            vm.error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 16.dp)) }
            Composer(streaming = vm.streaming, onSend = vm::send, onStop = vm::stop)
        }
    }
}

@Composable
private fun ModelPicker(vm: ChatViewModel) {
    var open by remember { mutableStateOf(false) }
    val current = vm.models.firstOrNull { it.id == vm.model }
    TextButton(onClick = { open = true }) {
        Text(current?.displayName ?: vm.model ?: "Model", maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 140.dp))
    }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        vm.models.forEach { model ->
            DropdownMenuItem(
                text = { Text(model.displayName ?: model.id) },
                onClick = { vm.pickModel(model.id); open = false },
            )
        }
    }
}

@Composable
private fun Bubble(turn: UiTurn, isLast: Boolean, onSuggestion: (String) -> Unit) {
    val user = turn.role == Role.USER
    Box(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), contentAlignment = if (user) Alignment.CenterEnd else Alignment.CenterStart) {
        Column(
            Modifier
                .widthIn(max = if (user) 320.dp else 600.dp)
                .background(
                    if (user) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainer,
                    RoundedCornerShape(16.dp),
                )
                .padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            if (turn.reasoning.isNotBlank()) Reasoning(turn.reasoning)
            turn.tools.forEach { tool ->
                Text(
                    "⚙ ${tool.name}" + if (tool.done) " ✓" else " …",
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            turn.attachments.forEach { Text("📎 ${it.fileName}", style = MaterialTheme.typography.labelMedium) }
            turn.images.forEach { _ -> Text("🖼 Generated image (open in web)", style = MaterialTheme.typography.labelMedium) }
            turn.todos.forEach { Text((if (it.done) "☑ " else "☐ ") + it.title, style = MaterialTheme.typography.bodySmall) }
            when {
                user -> Text(turn.text)
                turn.text.isNotBlank() -> Markdown(content = turn.text)
                !turn.complete -> Text("…")
            }
            turn.reauthServer?.let {
                Text("$it needs you to sign in again — open the web app to reconnect.", color = MaterialTheme.colorScheme.error)
            }
            if (turn.stopped) Text("Stopped", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (isLast && turn.suggestions.isNotEmpty()) {
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    turn.suggestions.forEach { s -> SuggestionChip(onClick = { onSuggestion(s) }, label = { Text(s) }) }
                }
            }
        }
    }
}

@Composable
private fun Reasoning(text: String) {
    var open by remember { mutableStateOf(false) }
    Column(Modifier.clickable { open = !open }) {
        Text(if (open) "Thinking ▾" else "Thinking ▸", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (open) Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun Composer(streaming: Boolean, onSend: (String) -> Unit, onStop: () -> Unit) {
    var text by remember { mutableStateOf("") }
    Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text("Message") },
            maxLines = 6,
            modifier = Modifier.weight(1f),
        )
        if (streaming) {
            OutlinedButton(onClick = onStop) { Text("Stop") }
        } else {
            Button(onClick = { onSend(text); text = "" }, enabled = text.isNotBlank()) { Text("Send") }
        }
    }
}
