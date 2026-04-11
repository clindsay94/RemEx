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
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.windowInsetsPadding
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
import androidx.compose.ui.res.stringResource
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.launch

private data class TutorialPage(
    val emoji: String,
    val titleRes: Int,
    val bodyRes: Int,
    val linkLabelRes: Int? = null,
    val linkUrl: String? = null
)

private val tutorialPages = listOf(
    TutorialPage(
        emoji = "🖥️",
        titleRes = R.string.tutorial_page1_title,
        bodyRes = R.string.tutorial_page1_body
    ),
    TutorialPage(
        emoji = "⚙️",
        titleRes = R.string.tutorial_page2_title,
        bodyRes = R.string.tutorial_page2_body,
        linkLabelRes = R.string.tutorial_page2_link_label,
        linkUrl = "https://github.com/clindsay94/RemEx/releases"
    ),
    TutorialPage(
        emoji = "📡",
        titleRes = R.string.tutorial_page3_title,
        bodyRes = R.string.tutorial_page3_body
    ),
    TutorialPage(
        emoji = "🔍",
        titleRes = R.string.tutorial_page4_title,
        bodyRes = R.string.tutorial_page4_body
    ),
    TutorialPage(
        emoji = "🔌",
        titleRes = R.string.tutorial_page5_title,
        bodyRes = R.string.tutorial_page5_body
    ),
    TutorialPage(
        emoji = "🚀",
        titleRes = R.string.tutorial_page6_title,
        bodyRes = R.string.tutorial_page6_body
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
                        text = stringResource(R.string.tutorial_skip),
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
                .windowInsetsPadding(WindowInsets.navigationBars)
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
                        Text(stringResource(R.string.tutorial_get_started))
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
                        Text(stringResource(R.string.tutorial_next))
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
            text = stringResource(page.titleRes),
            style = MaterialTheme.typography.headlineMedium,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.onBackground,
            textAlign = TextAlign.Center
        )

        Spacer(modifier = Modifier.height(16.dp))

        Text(
            text = stringResource(page.bodyRes),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            lineHeight = 24.sp
        )

        if (page.linkLabelRes != null && page.linkUrl != null) {
            Spacer(modifier = Modifier.height(16.dp))

            val linkColor = MaterialTheme.colorScheme.primary
            val linkLabel = stringResource(page.linkLabelRes)
            val annotatedString = buildAnnotatedString {
                pushStringAnnotation(tag = "URL", annotation = page.linkUrl)
                withStyle(
                    SpanStyle(
                        color = linkColor,
                        fontWeight = FontWeight.SemiBold,
                        textDecoration = TextDecoration.Underline
                    )
                ) {
                    append(linkLabel)
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
