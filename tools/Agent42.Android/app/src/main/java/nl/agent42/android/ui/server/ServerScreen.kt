package nl.agent42.android.ui.server

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import nl.agent42.android.AppContainer
import nl.agent42.android.session.Session

/** Which Agent42 to talk to: production (https, Easy Auth sign-in) or a local DevHost (http, no sign-in). */
@Composable
fun ServerScreen(container: AppContainer) {
    var url by remember { mutableStateOf(container.session?.baseUrl ?: Session.DEFAULT_URL) }
    val normalized = Session.normalizeUrl(url)
    Column(
        Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text("Agent 42", style = MaterialTheme.typography.headlineMedium)
        OutlinedTextField(
            value = url,
            onValueChange = { url = it },
            label = { Text("Server") },
            singleLine = true,
            isError = normalized == null,
            modifier = Modifier.fillMaxWidth(),
        )
        Text(
            "An https server signs you in with your Microsoft account. For a local DevHost, run " +
                "`adb reverse tcp:61326 tcp:61326` and use http://localhost:61326 (no sign-in).",
            style = MaterialTheme.typography.bodySmall,
        )
        Button(onClick = { normalized?.let(container::setServer) }, enabled = normalized != null) { Text("Continue") }
    }
}
