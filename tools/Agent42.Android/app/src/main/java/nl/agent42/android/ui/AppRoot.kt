package nl.agent42.android.ui

import androidx.activity.compose.BackHandler
import androidx.activity.compose.LocalActivity
import androidx.compose.runtime.Composable
import nl.agent42.android.AppContainer
import nl.agent42.android.Screen
import nl.agent42.android.ui.chat.ChatScreen
import nl.agent42.android.ui.conversations.ConversationsScreen
import nl.agent42.android.ui.server.ServerScreen
import nl.agent42.android.ui.signin.SignInScreen

@Composable
fun AppRoot(container: AppContainer) {
    val activity = LocalActivity.current
    BackHandler { if (!container.back()) activity?.finish() }
    when (val screen = container.stack.last()) {
        Screen.Server -> ServerScreen(container)
        Screen.SignIn -> SignInScreen(container)
        Screen.Conversations -> ConversationsScreen(container)
        is Screen.Chat -> ChatScreen(container, screen)
    }
}
