package nl.agent42.android.voice

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Person
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import nl.agent42.android.Agent42App
import nl.agent42.android.MainActivity

/**
 * Keeps the voice call alive with the screen off or another app in front: a microphone foreground service with an
 * ongoing notification (tap = back to the call, "Hang up" ends it). The call itself is the [VoiceSession] in the
 * app container; this service only holds the process and shows its state.
 */
class VoiceCallService : Service() {
    private val container get() = (application as Agent42App).container

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val session = container.voice
        if (intent?.action == ACTION_HANG_UP || session == null) {
            container.endVoice()
            stopSelf()
            return START_NOT_STICKY
        }
        // phoneCall: a self-managed Telecom call (core-telecom requires it on Android 14+); microphone: the recognizer
        // keeps listening with the screen off.
        startForeground(
            NOTIFICATION_ID,
            notification(session.title, label(session.state)),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_PHONE_CALL or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE,
        )
        session.onState = { state ->
            if (state == VoiceState.ENDED) {
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            } else {
                getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification(session.title, label(state)))
            }
        }
        return START_NOT_STICKY
    }

    private fun notification(title: String, text: String): Notification {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Voice call", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE,
        )
        val hangUp = PendingIntent.getService(
            this, 1, Intent(this, VoiceCallService::class.java).setAction(ACTION_HANG_UP), PendingIntent.FLAG_IMMUTABLE,
        )
        // A CallStyle notification is what Telecom expects for an ongoing call (within 5 s of adding it); it brings the
        // system's own Hang up button.
        val caller = Person.Builder().setName("Agent 42 · $title").setImportant(true).build()
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentText(text)
            .setOngoing(true)
            .setCategory(Notification.CATEGORY_CALL)
            .setContentIntent(open)
            .setStyle(Notification.CallStyle.forOngoingCall(caller, hangUp))
            .build()
    }

    private fun label(state: VoiceState) = when (state) {
        VoiceState.LISTENING -> "Listening"
        VoiceState.THINKING -> "Thinking"
        VoiceState.SPEAKING -> "Speaking"
        else -> "Voice call"
    }

    companion object {
        private const val CHANNEL = "voice-call"
        private const val NOTIFICATION_ID = 42
        private const val ACTION_HANG_UP = "nl.agent42.android.HANG_UP"

        fun start(context: Context) {
            context.startForegroundService(Intent(context, VoiceCallService::class.java))
        }
    }
}
