package nl.agent42.android.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf

val LocalAgent42Colors = staticCompositionLocalOf { DarkTokens }

/** The web app's tokens, for components that need more than Material's color roles. */
object Agent42 {
    val colors: Agent42Colors
        @Composable @ReadOnlyComposable get() = LocalAgent42Colors.current
}

/**
 * The Agent 42 look (ADR 0114 app, styles.css tokens): follows the system light/dark setting, never Material You's
 * dynamic colours, so the app looks like the website rather than like the wallpaper.
 */
@Composable
fun Agent42Theme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    val t = if (darkTheme) DarkTokens else LightTokens
    val scheme = if (darkTheme) {
        darkColorScheme(
            primary = t.amber, onPrimary = t.bg, primaryContainer = t.amberGlow, onPrimaryContainer = t.amber,
            secondary = t.inkDim, onSecondary = t.bg, secondaryContainer = t.lineSoft, onSecondaryContainer = t.ink,
            tertiary = t.green, background = t.bg, onBackground = t.ink, surface = t.bg, onSurface = t.ink,
            surfaceVariant = t.bgRaised, onSurfaceVariant = t.inkDim, surfaceContainer = t.bgRaised,
            surfaceContainerHigh = t.bgRaised, surfaceContainerHighest = t.lineSoft, surfaceContainerLow = t.bgInset,
            outline = t.line, outlineVariant = t.lineSoft, error = t.red, onError = t.bg,
        )
    } else {
        lightColorScheme(
            primary = t.amber, onPrimary = t.bgRaised, primaryContainer = t.amberGlow, onPrimaryContainer = t.amberDeep,
            secondary = t.inkDim, onSecondary = t.bgRaised, secondaryContainer = t.lineSoft, onSecondaryContainer = t.ink,
            tertiary = t.green, background = t.bg, onBackground = t.ink, surface = t.bg, onSurface = t.ink,
            surfaceVariant = t.bgRaised, onSurfaceVariant = t.inkDim, surfaceContainer = t.bgRaised,
            surfaceContainerHigh = t.bgRaised, surfaceContainerHighest = t.lineSoft, surfaceContainerLow = t.bgInset,
            outline = t.line, outlineVariant = t.lineSoft, error = t.red, onError = t.bgRaised,
        )
    }
    CompositionLocalProvider(LocalAgent42Colors provides t) {
        MaterialTheme(colorScheme = scheme, typography = Typography, content = content)
    }
}
