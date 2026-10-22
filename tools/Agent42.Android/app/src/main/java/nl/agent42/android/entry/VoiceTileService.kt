package nl.agent42.android.entry

import android.app.PendingIntent
import android.service.quicksettings.TileService
import nl.agent42.android.MainActivity

/** Quick Settings tile: one tap opens a voice call (ADR 0114 phase 2). */
class VoiceTileService : TileService() {
    override fun onClick() {
        super.onClick()
        val open = PendingIntent.getActivity(this, 0, MainActivity.voiceCallIntent(this), PendingIntent.FLAG_IMMUTABLE)
        // Collapses the shade and, with the phone locked, asks to unlock first.
        startActivityAndCollapse(open)
    }
}
