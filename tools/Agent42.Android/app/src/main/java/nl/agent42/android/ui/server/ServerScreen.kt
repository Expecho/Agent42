package nl.agent42.android.ui.server

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.MaterialTheme
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
import nl.agent42.android.theme.Agent42
import nl.agent42.android.ui.Agent42Background
import nl.agent42.android.ui.AmberButton
import nl.agent42.android.ui.BrandHeader
import nl.agent42.android.ui.FramedField
import nl.agent42.android.ui.MetaLabel

/** Which Agent42 to talk to: production (https, Easy Auth sign-in) or a local DevHost (http, no sign-in). */
@Composable
fun ServerScreen(container: AppContainer) {
    var url by remember { mutableStateOf(container.session?.baseUrl ?: Session.DEFAULT_URL) }
    val normalized = Session.normalizeUrl(url)
    val c = Agent42.colors
    Agent42Background {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().padding(28.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            BrandHeader(status = null)
            Spacer(Modifier.height(36.dp))
            Text("Where does your agent live?", style = MaterialTheme.typography.headlineMedium, color = c.ink)
            MetaLabel("SERVER")
            FramedField(url, { url = it }, Session.DEFAULT_URL, Modifier.fillMaxWidth())
            if (normalized == null) Text("That isn't a server address.", style = MaterialTheme.typography.bodySmall, color = c.red)
            Text(
                "An https server signs you in with your Microsoft account. For a local DevHost, run " +
                    "adb reverse tcp:61326 tcp:61326 and use http://localhost:61326 — no sign-in.",
                style = MaterialTheme.typography.bodySmall,
                color = c.inkDim,
            )
            Spacer(Modifier.height(8.dp))
            AmberButton("Connect", onClick = { normalized?.let(container::setServer) }, enabled = normalized != null, modifier = Modifier.fillMaxWidth())
        }
    }
}
