package nl.agent42.android.ui.chat

import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import androidx.core.net.toUri
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.lifecycle.viewmodel.compose.viewModel
import com.mikepenz.markdown.m3.Markdown
import com.mikepenz.markdown.m3.markdownColor
import com.mikepenz.markdown.m3.markdownTypography
import com.mikepenz.markdown.model.markdownPadding
import java.io.File
import java.util.UUID
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.chat.Role
import nl.agent42.android.chat.UiTurn
import nl.agent42.android.location.LocationSource
import nl.agent42.android.theme.Agent42
import nl.agent42.android.theme.Agent42Fonts
import nl.agent42.android.theme.Prose
import nl.agent42.android.ui.Agent42Background
import nl.agent42.android.ui.AmberButton
import nl.agent42.android.ui.FramedField
import nl.agent42.android.ui.GhostAction
import nl.agent42.android.ui.LineButton
import nl.agent42.android.ui.MetaLabel
import nl.agent42.android.ui.ToolChip

@Composable
fun ChatScreen(container: AppContainer, chat: Screen.Chat) {
    val vm = viewModel(key = "chat-${chat.id}") { ChatViewModel(container, chat) }
    val context = LocalContext.current
    val list = rememberLazyListState()
    val c = Agent42.colors

    // Only an on-screen chat advertises client tools: it can show a dialog and take a while-in-use fix (ADR 0114).
    LifecycleResumeEffect(vm) {
        vm.foreground = true
        onPauseOrDispose { vm.foreground = false }
    }
    // A voice call on this chat added turns server-side: re-read them when it ends.
    LaunchedEffect(container.voiceEnded) {
        if (container.voiceEnded > 0) vm.refresh()
    }
    LaunchedEffect(vm.turns.size, vm.turns.lastOrNull()?.text?.length) {
        if (vm.turns.isNotEmpty()) list.animateScrollToItem(vm.turns.lastIndex)
    }

    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { grants ->
        vm.answerLocationPermission(grants.values.any { it })
    }
    LaunchedEffect(vm.locationPermission) {
        if (vm.locationPermission != null) permission.launch(LocationSource.PERMISSIONS)
    }
    vm.confirm?.let { request ->
        AlertDialog(
            onDismissRequest = { vm.answerConfirm(false) },
            containerColor = c.bgRaised,
            title = { Text("CONFIRM", style = MaterialTheme.typography.titleMedium, color = c.amber) },
            text = { Text(request.text, style = Prose, color = c.ink) },
            confirmButton = { AmberButton("approve", { vm.answerConfirm(true) }) },
            dismissButton = { LineButton("decline", { vm.answerConfirm(false) }) },
        )
    }

    Agent42Background {
        Column(Modifier.fillMaxSize().safeDrawingPadding().imePadding()) {
            Row(
                Modifier.fillMaxWidth().padding(start = 4.dp, end = 4.dp, top = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                GhostAction("←", { container.back() }, color = c.ink)
                Text(chat.title, style = MaterialTheme.typography.titleSmall, color = c.ink, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                if (!chat.agentBound) ModelPicker(vm)
                GhostAction("🎙", { container.navigate(Screen.Voice(chat)) }, enabled = !vm.streaming, color = c.amber)
                // The web app has no per-conversation link, so this opens its home page (ADR 0114: rich content such as
                // artifacts and mermaid lives there).
                GhostAction("web", {
                    container.session?.baseUrl?.let { context.startActivity(Intent(Intent.ACTION_VIEW, it.toUri())) }
                })
            }
            Box(Modifier.fillMaxWidth().padding(horizontal = 16.dp).height(1.dp).background(c.lineSoft))
            Box(Modifier.weight(1f).fillMaxWidth()) {
                if (vm.turns.isEmpty() && !vm.streaming) EmptyState()
                LazyColumn(state = list, modifier = Modifier.fillMaxSize(), contentPadding = PaddingValues(vertical = 16.dp)) {
                    itemsIndexed(vm.turns) { index, turn -> Message(vm, turn, isLast = index == vm.turns.lastIndex) }
                }
            }
            vm.error?.let { Text(it, color = c.red, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 20.dp)) }
            vm.notice?.let { Text(it, color = c.inkDim, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 20.dp)) }
            PendingFiles(vm)
            Composer(vm, container)
        }
    }
}

/** `.empty`: the outlined "42", the serif line, the dim hint. */
@Composable
private fun EmptyState() {
    val c = Agent42.colors
    Column(Modifier.fillMaxSize().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
        Text("42", style = MaterialTheme.typography.displayLarge.copy(color = c.amberDeep, drawStyle = Stroke(width = 3f)))
        Text("The answer is ready.", style = MaterialTheme.typography.headlineSmall, color = c.ink, modifier = Modifier.padding(top = 12.dp, bottom = 8.dp))
        Text(
            "Now ask the question — by text, or tap 🎙 to talk.",
            style = MaterialTheme.typography.bodySmall,
            color = c.inkDim,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
private fun ModelPicker(vm: ChatViewModel) {
    var open by remember { mutableStateOf(false) }
    val c = Agent42.colors
    val current = vm.models.firstOrNull { it.id == vm.model }
    Box {
        Text(
            (current?.displayName ?: vm.model ?: "model") + " ▾",
            style = MaterialTheme.typography.labelSmall,
            color = c.inkDim,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier
                .widthIn(max = 130.dp)
                .clip(RoundedCornerShape(6.dp))
                .clickable { open = true }
                .border(1.dp, c.line, RoundedCornerShape(6.dp))
                .padding(horizontal = 8.dp, vertical = 5.dp),
        )
        DropdownMenu(expanded = open, onDismissRequest = { open = false }, containerColor = c.bgRaised) {
            vm.models.forEach { model ->
                DropdownMenuItem(
                    text = { Text(model.displayName ?: model.id, style = MaterialTheme.typography.bodyMedium, color = if (model.id == vm.model) c.amber else c.ink) },
                    onClick = { vm.pickModel(model.id); open = false },
                )
            }
        }
    }
}

/** `.msg`: the meta label, then a bordered mono bubble (user) or serif prose without a bubble (assistant). */
@Composable
private fun Message(vm: ChatViewModel, turn: UiTurn, isLast: Boolean) {
    val c = Agent42.colors
    val user = turn.role == Role.USER
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 18.dp, vertical = 10.dp),
        horizontalAlignment = if (user) Alignment.End else Alignment.Start,
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        MetaLabel(if (user) "YOU" else "AGENT 42")
        turn.attachments.filter { it.mediaType.startsWith("image/") }.forEach { AttachmentImage(vm, it.id) }
        turn.attachments.filterNot { it.mediaType.startsWith("image/") }.forEach {
            Text("📎 ${it.fileName}", style = MaterialTheme.typography.labelMedium, color = c.inkDim)
        }
        if (user) {
            Text(
                turn.text,
                style = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 13.5.sp, lineHeight = 20.sp, color = c.ink),
                modifier = Modifier
                    .widthIn(max = 320.dp)
                    .background(c.bgRaised, UserBubble)
                    .border(1.dp, c.line, UserBubble)
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            )
            return@Column
        }
        if (turn.reasoning.isNotBlank()) Reasoning(turn.reasoning)
        if (turn.tools.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) { turn.tools.forEach { ToolChip(it.name, it.done) } }
        }
        turn.images.forEach { AttachmentImage(vm, it) }
        turn.todos.forEach {
            Text((if (it.done) "☑ " else "☐ ") + it.title, style = MaterialTheme.typography.bodySmall, color = if (it.done) c.inkFaint else c.ink)
        }
        when {
            turn.text.isNotBlank() -> Markdown(content = turn.text, colors = proseColors(), typography = proseTypography(), padding = prosePadding())
            !turn.complete -> Text("▍", style = Prose, color = c.amber)
        }
        turn.reauthServer?.let {
            Text("$it needs you to sign in again — open the web app to reconnect.", style = MaterialTheme.typography.bodySmall, color = c.red)
        }
        if (turn.stopped) MetaLabel("STOPPED")
        if (isLast && turn.suggestions.isNotEmpty()) {
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                turn.suggestions.forEach { s ->
                    Text(
                        s,
                        style = MaterialTheme.typography.bodySmall,
                        color = c.inkDim,
                        modifier = Modifier
                            .clip(RoundedCornerShape(16.dp))
                            .clickable { vm.send(s) }
                            .border(1.dp, c.line, RoundedCornerShape(16.dp))
                            .padding(horizontal = 12.dp, vertical = 7.dp),
                    )
                }
            }
        }
    }
}

private val UserBubble = RoundedCornerShape(topStart = 14.dp, topEnd = 14.dp, bottomEnd = 4.dp, bottomStart = 14.dp)

// The web's prose rhythm: a paragraph's worth of air between blocks (the renderer's default of 2 dp runs them together).
@Composable
private fun prosePadding() = markdownPadding(block = 10.dp, list = 6.dp, codeBlock = PaddingValues(12.dp))

@Composable
private fun proseColors() = Agent42.colors.let { c ->
    markdownColor(text = c.ink, codeBackground = c.bgInset, inlineCodeBackground = c.bgInset, dividerColor = c.line, tableBackground = c.bgInset)
}

/** The web's `.prose`: Newsreader for text, Plex Mono for code, amber links. */
@Composable
private fun proseTypography() = Agent42.colors.let { c ->
    val h = Prose.copy(fontWeight = FontWeight.SemiBold, color = c.ink)
    val code = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 13.sp, lineHeight = 19.sp, color = c.ink)
    markdownTypography(
        h1 = h.copy(fontSize = 25.sp, lineHeight = 31.sp),
        h2 = h.copy(fontSize = 22.sp, lineHeight = 28.sp),
        h3 = h.copy(fontSize = 19.sp, lineHeight = 25.sp),
        h4 = h.copy(fontSize = 17.sp),
        h5 = h.copy(fontSize = 16.sp),
        h6 = h.copy(fontSize = 15.sp),
        text = Prose.copy(color = c.ink),
        code = code,
        inlineCode = code.copy(fontSize = 14.sp, color = c.amber),
        quote = Prose.copy(color = c.inkDim, fontStyle = androidx.compose.ui.text.font.FontStyle.Italic),
        paragraph = Prose.copy(color = c.ink),
        ordered = Prose.copy(color = c.ink),
        bullet = Prose.copy(color = c.ink),
        list = Prose.copy(color = c.ink),
        textLink = TextLinkStyles(Prose.copy(color = c.amber, textDecoration = TextDecoration.Underline).toSpanStyle()),
    )
}

@Composable
private fun AttachmentImage(vm: ChatViewModel, attachmentId: String) {
    val c = Agent42.colors
    val bitmap by produceState<android.graphics.Bitmap?>(null, attachmentId) {
        value = vm.image(attachmentId)?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
    }
    bitmap?.let {
        Image(
            it.asImageBitmap(),
            contentDescription = null,
            contentScale = ContentScale.Fit,
            modifier = Modifier
                .widthIn(max = 260.dp)
                .heightIn(max = 260.dp)
                .clip(RoundedCornerShape(10.dp))
                .border(1.dp, c.line, RoundedCornerShape(10.dp)),
        )
    } ?: Text("image …", style = MaterialTheme.typography.labelSmall, color = c.inkFaint)
}

/** Reasoning (ADR 0071): collapsed behind a faint "thinking" line with an amber left rule when open. */
@Composable
private fun Reasoning(text: String) {
    var open by remember { mutableStateOf(false) }
    val c = Agent42.colors
    Column(Modifier.clickable { open = !open }) {
        Text(if (open) "thinking ▾" else "thinking ▸", style = MaterialTheme.typography.labelSmall, color = c.inkFaint)
        if (open) {
            Row(Modifier.padding(top = 6.dp)) {
                Box(Modifier.width(2.dp).heightIn(min = 20.dp).background(c.amberDeep))
                Text(text, style = MaterialTheme.typography.bodySmall, color = c.inkDim, modifier = Modifier.padding(start = 10.dp))
            }
        }
    }
}

@Composable
private fun PendingFiles(vm: ChatViewModel) {
    if (vm.files.isEmpty()) return
    val c = Agent42.colors
    Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        vm.files.forEach { file ->
            Text(
                when {
                    file.error != null -> "${file.name}: ${file.error}"
                    file.uploading -> "${file.name} …"
                    else -> "${file.name}  ✕"
                },
                style = MaterialTheme.typography.labelSmall,
                color = if (file.error != null) c.red else c.inkDim,
                maxLines = 1,
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .clickable { vm.removeFile(file.key) }
                    .background(c.bgInset, RoundedCornerShape(8.dp))
                    .border(1.dp, c.line, RoundedCornerShape(8.dp))
                    .padding(horizontal = 10.dp, vertical = 6.dp),
            )
        }
    }
}

/** `.composer`: one bordered frame holding attach, camera, the prompt and the send/stop button. */
@Composable
private fun Composer(vm: ChatViewModel, container: AppContainer) {
    val context = LocalContext.current
    val c = Agent42.colors
    var cameraUri by remember { mutableStateOf<Uri?>(null) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris -> uris.forEach(vm::attach) }
    val camera = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { saved ->
        if (saved) cameraUri?.let(vm::attach)
    }
    FramedField(
        value = vm.draft,
        onValueChange = { vm.draft = it },
        placeholder = "Transmit a prompt…",
        singleLine = false,
        modifier = Modifier.fillMaxWidth().padding(start = 12.dp, end = 12.dp, top = 6.dp, bottom = 10.dp),
        leading = {
            Row {
                GhostAction("📎", { pick.launch(arrayOf("*/*")) }, enabled = !vm.streaming)
                GhostAction("📷", {
                    val file = File(container.cacheDir, "camera/${UUID.randomUUID()}.jpg").apply { parentFile?.mkdirs() }
                    val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
                    cameraUri = uri
                    camera.launch(uri)
                }, enabled = !vm.streaming)
            }
        },
        trailing = {
            if (vm.streaming) {
                LineButton("stop", vm::stop, color = c.red)
            } else {
                AmberButton("↑", { vm.send() }, enabled = vm.draft.isNotBlank() && vm.files.none { it.uploading }, contentPadding = PaddingValues(horizontal = 14.dp, vertical = 8.dp))
            }
        },
    )
}
