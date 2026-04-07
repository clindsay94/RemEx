package com.clindsay94.remex.ui.screens

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.ClickableText
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.launch

private data class TutorialPage(
    val emoji: String,
    val title: String,
    val body: String,
    val linkLabel: String? = null,
    val linkUrl: String? = null
)

private val tutorialPages = listOf(
    TutorialPage(
        emoji = "🖥️",
        title = "Welcome to RemEx",
        body = "RemEx is your remote PC management command center. " +
                "Control your computer, launch apps, monitor performance, " +
                "and even view your desktop — all from your phone.\n\n" +
                "Let's get you connected in a few quick steps."
    ),
    TutorialPage(
        emoji = "⚙️",
        title = "Install the Host First",
        body = "Before you can connect, you need the RemEx host software " +
                "running on your PC.\n\n" +
                "Option 1 — RemEx Desktop Client\n" +
                "Download and run Remex.Client.Desktop on your PC. It bundles " +
                "the host automatically — just launch it and the host starts " +
                "in the background.\n\n" +
                "Option 2 — RemEx Host (standalone)\n" +
                "Install and run Remex.Host as a standalone service. This is " +
                "ideal for headless servers or if you only need the host.\n\n" +
                "Both are self-contained — no extra runtimes needed. Download " +
                "the latest publish folder from the GitHub releases page, " +
                "extract, and run.",
        linkLabel = "Download from GitHub Releases",
        linkUrl = "https://github.com/clindsay94/RemEx/releases"
    ),
    TutorialPage(
        emoji = "📡",
        title = "Auto-Discovery",
        body = "The easiest way to connect is automatic discovery.\n\n" +
                "1. Make sure Remex.Client.Desktop or Remex.Host is running " +
                "on your PC\n" +
                "2. Ensure both your phone and PC are on the same Wi-Fi network\n" +
                "3. Tap 'Discover Automatically' on the Connection screen\n\n" +
                "RemEx uses mDNS to detect running hosts on your local " +
                "network. Your PC's IP and port will be filled in automatically."
    ),
    TutorialPage(
        emoji = "🔍",
        title = "Finding Your IP Address",
        body = "If auto-discovery doesn't work, you can connect manually. " +
                "First, find your PC's local IP address:\n\n" +
                "Windows:\n" +
                "  Open Command Prompt → type ipconfig\n" +
                "  Look for 'IPv4 Address' (e.g. 192.168.1.42)\n\n" +
                "macOS:\n" +
                "  System Settings → Network, or run:\n" +
                "  ipconfig getifaddr en0\n\n" +
                "Linux:\n" +
                "  Run ip addr or hostname -I\n\n" +
                "You can also see the IP address in the Remex.Client.Desktop " +
                "window under the connection status section."
    ),
    TutorialPage(
        emoji = "🔌",
        title = "Manual Connection",
        body = "Once you have your IP address:\n\n" +
                "1. Go to the Connection screen (Settings → Connection)\n" +
                "2. Enter your PC's IP address\n" +
                "3. Enter the port (default is 5005)\n" +
                "4. Tap 'Save & Connect'\n\n" +
                "RemEx will establish a connection and you'll see your PC's " +
                "status appear on the dashboard.\n\n" +
                "Remember: Remex.Client.Desktop or Remex.Host must be running " +
                "on the target PC for the connection to succeed."
    ),
    TutorialPage(
        emoji = "🚀",
        title = "You're All Set!",
        body = "You're ready to take control.\n\n" +
                "From the dashboard you can monitor system stats, launch apps, " +
                "manage tasks, and stream your desktop remotely.\n\n" +
                "Head to Personalization to customize your theme, card shapes, " +
                "fonts, and colors.\n\n" +
                "Check out the FAQ in Settings if you run into any issues. " +
                "Enjoy RemEx!"
    )
)

@Composable
fun TutorialScreen(
    onFinished: () -> Unit
) {
    val context = LocalContext.current
    val settingsManager = remember { SettingsManager(context) }
    val scope = rememberCoroutineScope()
    val pagerState = rememberPagerState(pageCount = { tutorialPages.size })
    val isLastPage = pagerState.currentPage == tutorialPages.size - 1

    fun completeTutorial() {
        scope.launch {
            settingsManager.markOnboardingCompleted()
            onFinished()
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
    ) {
        // Skip button — top right
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 16.dp, end = 8.dp),
            horizontalArrangement = Arrangement.End
        ) {
            TextButton(onClick = { completeTutorial() }) {
                Text(
                    text = "Skip",
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }

        // Pager content
        HorizontalPager(
            state = pagerState,
            modifier = Modifier.weight(1f)
        ) { page ->
            TutorialPageContent(tutorialPages[page])
        }

        // Page indicator dots
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 16.dp),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically
        ) {
            repeat(tutorialPages.size) { index ->
                val isSelected = pagerState.currentPage == index

                val width by animateDpAsState(
                    targetValue = if (isSelected) 24.dp else 8.dp,
                    animationSpec = tween(300),
                    label = "dotWidth"
                )
                val color by animateColorAsState(
                    targetValue = if (isSelected)
                        MaterialTheme.colorScheme.primary
                    else
                        MaterialTheme.colorScheme.outlineVariant,
                    animationSpec = tween(300),
                    label = "dotColor"
                )

                Box(
                    modifier = Modifier
                        .padding(horizontal = 4.dp)
                        .height(8.dp)
                        .width(width)
                        .clip(CircleShape)
                        .background(color)
                )
            }
        }

        // Bottom navigation button
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 24.dp, end = 24.dp, bottom = 32.dp),
            horizontalArrangement = Arrangement.Center
        ) {
            AnimatedContent(
                targetState = isLastPage,
                label = "buttonTransition"
            ) { lastPage ->
                if (lastPage) {
                    Button(
                        onClick = { completeTutorial() },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("Get Started")
                    }
                } else {
                    Button(
                        onClick = {
                            scope.launch {
                                pagerState.animateScrollToPage(pagerState.currentPage + 1)
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text("Next")
                    }
                }
            }
        }
    }
}

@Composable
private fun TutorialPageContent(page: TutorialPage) {
    val uriHandler = LocalUriHandler.current

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text(
            text = page.emoji,
            fontSize = 72.sp
        )

        Spacer(modifier = Modifier.height(24.dp))

        Text(
            text = page.title,
            style = MaterialTheme.typography.headlineMedium,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.onBackground,
            textAlign = TextAlign.Center
        )

        Spacer(modifier = Modifier.height(16.dp))

        Text(
            text = page.body,
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            lineHeight = 24.sp
        )

        if (page.linkLabel != null && page.linkUrl != null) {
            Spacer(modifier = Modifier.height(16.dp))

            val linkColor = MaterialTheme.colorScheme.primary
            val annotatedString = buildAnnotatedString {
                pushStringAnnotation(tag = "URL", annotation = page.linkUrl)
                withStyle(
                    SpanStyle(
                        color = linkColor,
                        fontWeight = FontWeight.SemiBold,
                        textDecoration = TextDecoration.Underline
                    )
                ) {
                    append(page.linkLabel)
                }
                pop()
            }

            ClickableText(
                text = annotatedString,
                style = MaterialTheme.typography.bodyLarge.copy(textAlign = TextAlign.Center),
                onClick = { offset ->
                    annotatedString.getStringAnnotations("URL", offset, offset)
                        .firstOrNull()?.let { uriHandler.openUri(it.item) }
                }
            )
        }
    }
}
