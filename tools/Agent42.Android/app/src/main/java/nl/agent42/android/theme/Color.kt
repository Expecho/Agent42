package nl.agent42.android.theme

import androidx.compose.runtime.Immutable
import androidx.compose.ui.graphics.Color

/**
 * The web app's design tokens (wwwroot/styles.css `:root`), so the app reads as the same product: a warm near-black
 * "terminal" with an amber accent, and a paper-coloured light theme with the amber darkened for contrast.
 */
@Immutable
data class Agent42Colors(
    val bg: Color,
    val bgRaised: Color,
    val bgInset: Color,
    val line: Color,
    val lineSoft: Color,
    val ink: Color,
    val inkDim: Color,
    val inkFaint: Color,
    val amber: Color,
    val amberDeep: Color,
    val amberGlow: Color,
    val green: Color,
    val red: Color,
    val dark: Boolean,
)

val DarkTokens = Agent42Colors(
    bg = Color(0xFF100F0D),
    bgRaised = Color(0xFF171512),
    bgInset = Color(0xFF0B0A09),
    line = Color(0xFF2A2620),
    lineSoft = Color(0xFF201D18),
    ink = Color(0xFFE9E2D3),
    inkDim = Color(0xFFA89F8C),
    inkFaint = Color(0xFF6D6555),
    amber = Color(0xFFFFB13D),
    amberDeep = Color(0xFFC97F1B),
    amberGlow = Color(0x24FFB13D),
    green = Color(0xFF9AC06D),
    red = Color(0xFFE06C5F),
    dark = true,
)

val LightTokens = Agent42Colors(
    bg = Color(0xFFF4EFE4),
    bgRaised = Color(0xFFFBF7EE),
    bgInset = Color(0xFFE9E2D2),
    line = Color(0xFFD6CDB8),
    lineSoft = Color(0xFFE2DAC7),
    ink = Color(0xFF2A2620),
    inkDim = Color(0xFF5C5647),
    inkFaint = Color(0xFF8A8270),
    amber = Color(0xFFA86611),
    amberDeep = Color(0xFF7F4E0A),
    amberGlow = Color(0x1FA86611),
    green = Color(0xFF5A7D3A),
    red = Color(0xFFBF3C30),
    dark = false,
)
