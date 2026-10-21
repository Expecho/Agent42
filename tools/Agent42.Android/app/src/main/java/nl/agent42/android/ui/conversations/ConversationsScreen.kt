package nl.agent42.android.ui.conversations

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.UUID
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.api.ConversationInfo

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConversationsScreen(container: AppContainer) {
    val vm = viewModel { ConversationsViewModel(container) }
    LaunchedEffect(Unit) { vm.load() }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Agent 42") },
                actions = {
                    TextButton(onClick = { container.navigate(Screen.Server) }) { Text("Server") }
                    TextButton(onClick = { container.signedOut() }) { Text("Sign out") }
                },
            )
        },
        floatingActionButton = {
            ExtendedFloatingActionButton(onClick = {
                container.navigate(Screen.Chat(UUID.randomUUID().toString(), "New chat", isNew = true, agentBound = false))
            }) { Text("New chat") }
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            OutlinedTextField(
                value = vm.query,
                onValueChange = vm::search,
                placeholder = { Text("Search chats") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            )
            if (vm.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            vm.error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
            LazyColumn {
                if (vm.query.isNotBlank()) {
                    items(vm.hits) { hit ->
                        ListItem(
                            headlineContent = { Text(vm.titleOf(hit.conversationId), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                            supportingContent = { Text(hit.text, maxLines = 2, overflow = TextOverflow.Ellipsis) },
                            modifier = Modifier.clickable { open(container, vm, hit.conversationId) },
                        )
                        HorizontalDivider()
                    }
                } else {
                    items(vm.conversations, key = { it.id }) { conversation ->
                        ConversationRow(conversation) { open(container, vm, conversation.id) }
                        HorizontalDivider()
                    }
                }
            }
        }
    }
}

@Composable
private fun ConversationRow(conversation: ConversationInfo, onClick: () -> Unit) {
    ListItem(
        headlineContent = {
            Text((if (conversation.pinned) "📌 " else "") + conversation.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
        },
        supportingContent = {
            Text(listOfNotNull(conversation.agentName, shortDate(conversation.updatedAt)).joinToString(" · "))
        },
        modifier = Modifier.clickable(onClick = onClick),
    )
}

private fun open(container: AppContainer, vm: ConversationsViewModel, id: String) {
    val info = vm.conversations.firstOrNull { it.id == id }
    container.navigate(Screen.Chat(id, info?.title ?: "Chat", isNew = false, agentBound = info?.agentId != null))
}

private fun shortDate(iso: String): String? = runCatching {
    OffsetDateTime.parse(iso).atZoneSameInstant(ZoneId.systemDefault())
        .format(DateTimeFormatter.ofLocalizedDateTime(FormatStyle.SHORT))
}.getOrNull()
