package nl.agent42.android.location

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.location.Location
import android.location.LocationManager
import android.os.CancellationSignal
import java.time.Instant
import kotlin.coroutines.resume
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import nl.agent42.android.chat.Fix

/**
 * One position for `get_current_location`, from the platform LocationManager (no Play Services). Like the web client
 * (location.js): a recent fix is answered at once, otherwise ONE capture bounded below the server's 15 s wait, and
 * never a stale fix.
 */
class LocationSource(private val context: Context) {
    private val manager = context.getSystemService(LocationManager::class.java)

    fun granted(): Boolean = PERMISSIONS.any {
        context.checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED
    }

    @Suppress("MissingPermission")
    suspend fun fix(): Fix? {
        if (!granted()) return null
        val provider = LocationManager.FUSED_PROVIDER.takeIf { manager.hasProvider(it) } ?: LocationManager.NETWORK_PROVIDER
        manager.getLastKnownLocation(provider)?.takeIf { isFresh(it) }?.let { return it.toFix() }
        return withTimeoutOrNull(CAPTURE_TIMEOUT_MS) {
            suspendCancellableCoroutine { cont ->
                val cancel = CancellationSignal()
                cont.invokeOnCancellation { cancel.cancel() }
                manager.getCurrentLocation(provider, cancel, context.mainExecutor) { location -> cont.resume(location?.toFix()) }
            }
        }
    }

    private fun isFresh(location: Location) = System.currentTimeMillis() - location.time < MAX_AGE_MS

    private fun Location.toFix() = Fix(
        latitude = latitude,
        longitude = longitude,
        accuracyMetres = if (hasAccuracy()) accuracy.toDouble() else null,
        capturedAt = Instant.ofEpochMilli(time),
    )

    companion object {
        val PERMISSIONS = arrayOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)
        private const val MAX_AGE_MS = 2 * 60 * 1000L
        private const val CAPTURE_TIMEOUT_MS = 10_000L
    }
}
