package nl.agent42.android.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import nl.agent42.android.theme.Agent42
import nl.agent42.android.theme.Agent42Fonts
import nl.agent42.android.theme.Meta

// The web app's recurring components (styles.css), as Compose pieces the screens share.

/** The page: `--bg` with the faint amber glow from the top right that the web body carries. */
@Composable
fun Agent42Background(content: @Composable BoxScope.() -> Unit) {
    val c = Agent42.colors
    Box(
        Modifier
            .fillMaxSize()
            .background(c.bg)
            .drawBehind {
                drawRect(
                    Brush.radialGradient(
                        listOf(c.amber.copy(alpha = if (c.dark) 0.06f else 0.08f), Color.Transparent),
                        center = Offset(size.width * 0.75f, -size.height * 0.05f),
                        radius = size.width * 0.9f,
                    ),
                )
            },
        content = content,
    )
}

/** `.brand__mark`: "42" in an amber-bordered, softly glowing tile. */
@Composable
fun BrandMark(size: Dp = 40.dp) {
    val c = Agent42.colors
    Box(
        Modifier
            .size(size)
            .drawBehind {
                drawRoundRect(
                    Brush.radialGradient(listOf(c.amberGlow, Color.Transparent), radius = this.size.width * 0.9f),
                    cornerRadius = CornerRadius(8.dp.toPx()),
                )
            }
            .background(Brush.linearGradient(listOf(c.amberGlow, Color.Transparent)), RoundedCornerShape(8.dp))
            .border(1.dp, c.amberDeep, RoundedCornerShape(8.dp)),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            "42",
            style = TextStyle(
                fontFamily = Agent42Fonts.mono,
                fontWeight = FontWeight.SemiBold,
                fontSize = (size.value * 0.41f).sp,
                letterSpacing = (-0.05).em,
                color = c.amber,
                shadow = Shadow(c.amber.copy(alpha = 0.55f), blurRadius = 18f),
            ),
        )
    }
}

/** `.brand`: the mark, "AGENT 42" letter-spaced, and a status line with the pulsing green dot. */
@Composable
fun BrandHeader(status: String?, modifier: Modifier = Modifier) {
    val c = Agent42.colors
    Row(modifier, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        BrandMark()
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text("AGENT 42", style = TextStyle(fontFamily = Agent42Fonts.mono, fontWeight = FontWeight.SemiBold, fontSize = 13.sp, letterSpacing = 0.28.em, color = c.ink))
            status?.let {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    StatusDot(c.green, pulsing = true)
                    Text(it, style = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 11.sp, letterSpacing = 0.08.em, color = c.inkFaint))
                }
            }
        }
    }
}

/** `.dot` / `.tool-call__status`: a small glowing dot, optionally pulsing. */
@Composable
fun StatusDot(color: Color, pulsing: Boolean = false, size: Dp = 6.dp) {
    val alpha = if (pulsing) {
        val t = rememberInfiniteTransition(label = "dot")
        val a by t.animateFloat(1f, 0.35f, infiniteRepeatable(tween(1200), RepeatMode.Reverse), label = "dot-alpha")
        a
    } else {
        1f
    }
    Box(
        Modifier
            .size(size)
            .alpha(alpha)
            .drawBehind { drawCircle(color.copy(alpha = 0.35f), radius = this.size.minDimension) }
            .background(color, CircleShape),
    )
}

/** `.msg__meta`: "YOU" / "AGENT 42" above a message. */
@Composable
fun MetaLabel(text: String, modifier: Modifier = Modifier) {
    Text(text, style = Meta.copy(color = Agent42.colors.inkFaint), modifier = modifier)
}

/** `.tool-call`: dashed border on the inset background, a status dot (amber while running, green when done). */
@Composable
fun ToolChip(name: String, done: Boolean) {
    val c = Agent42.colors
    Row(
        Modifier
            .background(c.bgInset, RoundedCornerShape(8.dp))
            .drawBehind {
                drawRoundRect(
                    c.line,
                    cornerRadius = CornerRadius(8.dp.toPx()),
                    style = Stroke(width = 1.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(5f, 4f))),
                )
            }
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(9.dp),
    ) {
        StatusDot(if (done) c.green else c.amber, pulsing = !done, size = 7.dp)
        Text(name, style = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 12.sp, color = c.inkDim))
    }
}

/** `.composer__frame`: raised, bordered, rounded; amber border + glow while focused. */
fun Modifier.frame(focused: Boolean, colors: nl.agent42.android.theme.Agent42Colors, radius: Dp = 14.dp): Modifier =
    this
        .drawBehind {
            if (focused) {
                drawRoundRect(colors.amberGlow, cornerRadius = CornerRadius((radius + 6.dp).toPx()), topLeft = Offset(-6.dp.toPx(), -6.dp.toPx()),
                    size = androidx.compose.ui.geometry.Size(size.width + 12.dp.toPx(), size.height + 12.dp.toPx()))
            }
        }
        .background(colors.bgRaised, RoundedCornerShape(radius))
        .border(1.dp, if (focused) colors.amberDeep else colors.line, RoundedCornerShape(radius))

/** A text input in a [frame]: the web's composer and search field look. */
@Composable
fun FramedField(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier,
    singleLine: Boolean = true,
    maxLines: Int = if (singleLine) 1 else 6,
    textStyle: TextStyle = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 14.sp, lineHeight = 21.sp),
    radius: Dp = 14.dp,
    leading: (@Composable () -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
) {
    val c = Agent42.colors
    var focused by remember { mutableStateOf(false) }
    Row(
        modifier.frame(focused, c, radius).padding(start = 14.dp, end = 6.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        leading?.invoke()
        Box(Modifier.weight(1f).padding(vertical = 8.dp)) {
            if (value.isEmpty()) Text(placeholder, style = textStyle.copy(color = c.inkFaint))
            androidx.compose.foundation.text.BasicTextField(
                value = value,
                onValueChange = onValueChange,
                singleLine = singleLine,
                maxLines = maxLines,
                textStyle = textStyle.copy(color = c.ink),
                cursorBrush = androidx.compose.ui.graphics.SolidColor(c.amber),
                modifier = Modifier
                    .fillMaxWidth()
                    .onFocusChanged { focused = it.isFocused },
            )
        }
        trailing?.invoke()
    }
}

/** A quiet text action in the web's mono style (top bars, links). */
@Composable
fun GhostAction(text: String, onClick: () -> Unit, enabled: Boolean = true, color: Color = Agent42.colors.inkDim) {
    Text(
        text,
        style = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 12.sp, letterSpacing = 0.08.em, color = if (enabled) color else Agent42.colors.inkFaint),
        modifier = Modifier
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 10.dp),
    )
}

/** The primary button: amber fill, dark ink, mono, like the web's send button. */
@Composable
fun AmberButton(text: String, onClick: () -> Unit, enabled: Boolean = true, modifier: Modifier = Modifier, contentPadding: PaddingValues = PaddingValues(horizontal = 16.dp, vertical = 10.dp)) {
    val c = Agent42.colors
    Box(
        modifier
            .background(if (enabled) c.amber else c.line, RoundedCornerShape(10.dp))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(contentPadding),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, style = TextStyle(fontFamily = Agent42Fonts.mono, fontWeight = FontWeight.SemiBold, fontSize = 13.sp, letterSpacing = 0.06.em, color = if (enabled) c.bg else c.inkFaint))
    }
}

/** A secondary button: bordered, transparent. */
@Composable
fun LineButton(text: String, onClick: () -> Unit, enabled: Boolean = true, modifier: Modifier = Modifier, color: Color = Agent42.colors.ink) {
    val c = Agent42.colors
    Box(
        modifier
            .border(1.dp, c.line, RoundedCornerShape(10.dp))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 10.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, style = TextStyle(fontFamily = Agent42Fonts.mono, fontSize = 13.sp, letterSpacing = 0.04.em, color = if (enabled) color else c.inkFaint))
    }
}
