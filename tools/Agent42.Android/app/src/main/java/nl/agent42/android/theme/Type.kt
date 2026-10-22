package nl.agent42.android.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import nl.agent42.android.R

/** The web app's two faces (styles.css --mono / --serif), bundled under the SIL OFL (assets/licenses). */
object Agent42Fonts {
    val mono = FontFamily(
        Font(R.font.ibm_plex_mono_regular, FontWeight.Normal),
        Font(R.font.ibm_plex_mono_medium, FontWeight.Medium),
        Font(R.font.ibm_plex_mono_semibold, FontWeight.SemiBold),
    )

    // Newsreader is a variable font; each weight is an instance of the same file.
    val serif = FontFamily(
        serif(FontWeight.Normal, FontStyle.Normal),
        serif(FontWeight.Medium, FontStyle.Normal),
        serif(FontWeight.SemiBold, FontStyle.Normal),
        serif(FontWeight.Normal, FontStyle.Italic),
    )

    private fun serif(weight: FontWeight, style: FontStyle) = Font(
        if (style == FontStyle.Italic) R.font.newsreader_italic else R.font.newsreader,
        weight,
        style,
        variationSettings = FontVariation.Settings(FontVariation.weight(weight.weight)),
    )
}

/** The assistant's prose, as the web's `.prose`: serif 17/1.65. */
val Prose = TextStyle(fontFamily = Agent42Fonts.serif, fontSize = 17.sp, lineHeight = 28.sp)

/** Small caps-like labels ("YOU", "AGENT 42", dates), as `.msg__meta`. */
val Meta = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 10.5.sp, letterSpacing = 0.22.em)

private fun mono(size: Int, line: Int, weight: FontWeight = FontWeight.Normal, spacing: Double = 0.0) =
    TextStyle(fontFamily = Agent42Fonts.mono, fontWeight = weight, fontSize = size.sp, lineHeight = line.sp, letterSpacing = spacing.em)

/** Everything else is IBM Plex Mono, like the web app's body. */
val Typography = Typography(
    displayLarge = TextStyle(fontFamily = Agent42Fonts.mono, fontWeight = FontWeight.SemiBold, fontSize = 120.sp, letterSpacing = (-0.06).em),
    headlineMedium = TextStyle(fontFamily = Agent42Fonts.serif, fontSize = 26.sp, lineHeight = 32.sp),
    headlineSmall = TextStyle(fontFamily = Agent42Fonts.serif, fontSize = 22.sp, lineHeight = 28.sp),
    titleLarge = mono(15, 20, FontWeight.SemiBold, 0.2),
    titleMedium = mono(13, 18, FontWeight.SemiBold, 0.28),
    titleSmall = mono(12, 16, FontWeight.Medium),
    bodyLarge = mono(14, 21),
    bodyMedium = mono(13, 19),
    bodySmall = mono(12, 17),
    labelLarge = mono(13, 18, FontWeight.Medium, 0.04),
    labelMedium = mono(12, 16),
    labelSmall = mono(11, 14, spacing = 0.08),
)
