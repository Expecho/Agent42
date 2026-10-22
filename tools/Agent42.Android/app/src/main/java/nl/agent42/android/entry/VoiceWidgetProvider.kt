package nl.agent42.android.entry

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.Context
import android.widget.RemoteViews
import nl.agent42.android.MainActivity
import nl.agent42.android.R

/** Home-screen widget: a single button that opens a voice call. Plain RemoteViews, no extra library. */
class VoiceWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        val open = PendingIntent.getActivity(context, 0, MainActivity.voiceCallIntent(context), PendingIntent.FLAG_IMMUTABLE)
        val views = RemoteViews(context.packageName, R.layout.widget_voice).apply {
            setOnClickPendingIntent(R.id.widget_voice_button, open)
        }
        ids.forEach { manager.updateAppWidget(it, views) }
    }
}
