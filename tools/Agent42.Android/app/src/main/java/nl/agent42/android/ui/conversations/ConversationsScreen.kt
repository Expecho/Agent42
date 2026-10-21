package nl.agent42.android.ui.conversations

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.theme.Agent42
import nl.agent42.android.theme.Meta
import nl.agent42.android.ui.Agent42Background
import nl.agent42.android.ui.AmberButton
import nl.agent42.android.ui.BrandHeader
import nl.agent42.android.ui.FramedField
import nl.agent42.android.ui.GhostAction
import nl.agent42.android.ui.LineButton
import nl.agent42.android.ui.MetaLabel

@Composable
fun ConversationsScreen(container: AppContainer) {
    val vm = viewModel { ConversationsViewModel(container) }
    val c = Agent42.colors
    LaunchedEffect(Unit) { vm.load() }
    Agent42Background {
        Column(Modifier.fillMaxSize().safeDrawingPadding()) {
            Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp, top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                BrandHeader(status = container.session?.baseUrl?.substringAfter("://"), modifier = Modifier.weight(1f))
                GhostAction("server", { container.navigate(Screen.Server) })
                GhostAction("sign out", { container.signedOut() })
            }
            FramedField(
                vm.query,
                vm::search,
                "search messages",
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 14.dp),
                radius = 10.dp,
                textStyle = MaterialTheme.typography.bodyMedium,
            )
            if (vm.loading) LinearProgressIndicator(Modifier.fillMaxWidth().height(1.dp), color = c.amber, trackColor = Color.Transparent)
            vm.error?.let { Text(it, color = c.red, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 20.dp)) }
            MetaLabel(if (vm.query.isBlank()) "CONVERSATIONS" else "RESULTS", Modifier.padding(start = 20.dp, top = 4.dp, bottom = 6.dp))
            Box(Modifier.weight(1f)) {
                LazyColumn(contentPadding = PaddingValues(start = 10.dp, end = 10.dp, bottom = 96.dp)) {
                    if (vm.query.isNotBlank()) {
                        items(vm.hits) { hit ->
                            ConversationRow(vm.titleOf(hit.conversationId), hit.text, pinned = false) { open(container, vm, hit.conversationId) }
                        }
                    } else {
                        items(vm.conversations, key = { it.id }) { conversation ->
                            ConversationRow(
                                conversation.title,
                                listOfNotNull(conversation.agentName, shortDate(conversation.updatedAt)).joinToString("  ·  "),
                                pinned = conversation.pinned,
                            ) { open(container, vm, conversation.id) }
                        }
                    }
                }
                Row(
                    Modifier
                        .align(Alignment.BottomCenter)
                        .fillMaxWidth()
                        // Opaque under the buttons, fading into the list above them, so no row shows through.
                        .background(Brush.verticalGradient(0f to Color.Transparent, 0.3f to c.bg, 1f to c.bg))
                        .padding(start = 16.dp, end = 16.dp, top = 28.dp, bottom = 16.dp),
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    AmberButton("+ new chat", { container.navigate(container.newChat()) }, modifier = Modifier.weight(1f), contentPadding = PaddingValues(vertical = 14.dp))
                    LineButton("🎙 voice", { container.voiceEntry() }, modifier = Modifier.weight(0.6f))
                }
            }
        }
    }
}

/** `.conv`: mono title over a faint, letter-spaced line; a pinned chat carries the amber left edge. */
@Composable
private fun ConversationRow(title: String, subtitle: String, pinned: Boolean, onClick: () -> Unit) {
    val c = Agent42.colors
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.width(2.dp).height(38.dp).background(if (pinned) c.amber else Color.Transparent, RoundedCornerShape(1.dp)))
        Column(Modifier.padding(start = 12.dp, top = 8.dp, bottom = 8.dp, end = 8.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(title, style = MaterialTheme.typography.bodyMedium, color = c.ink, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (subtitle.isNotBlank()) {
                Text(subtitle, style = Meta.copy(letterSpacing = Meta.letterSpacing * 0.3f), color = c.inkFaint, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

private fun open(container: AppContainer, vm: ConversationsViewModel, id: String) {
    val info = vm.conversations.firstOrNull { it.id == id }
    container.navigate(Screen.Chat(id, info?.title ?: "Chat", isNew = false, agentBound = info?.agentId != null))
}

private fun shortDate(iso: String): String? = runCatching {
    OffsetDateTime.parse(iso).atZoneSameInstant(ZoneId.systemDefault())
        .format(DateTimeFormatter.ofLocalizedDateTime(FormatStyle.SHORT))
}.getOrNull()
