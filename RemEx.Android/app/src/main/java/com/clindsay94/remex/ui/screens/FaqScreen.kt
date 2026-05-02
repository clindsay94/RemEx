package com.clindsay94.remex.ui.screens

import android.content.Intent
import android.net.Uri
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.*
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior

private data class FaqItem(
        val questionRes: Int,
        val answerRes: Int,
        val releasesLink: Boolean = false
)

private val faqItems =
        listOf(
                FaqItem(
                        questionRes = R.string.faq_q1,
                        answerRes = R.string.faq_a1,
                        releasesLink = true
                ),
                FaqItem(questionRes = R.string.faq_q2, answerRes = R.string.faq_a2),
                FaqItem(questionRes = R.string.faq_q3, answerRes = R.string.faq_a3),
                FaqItem(questionRes = R.string.faq_q4, answerRes = R.string.faq_a4),
                FaqItem(questionRes = R.string.faq_q5, answerRes = R.string.faq_a5),
                FaqItem(questionRes = R.string.faq_q6, answerRes = R.string.faq_a6),
                FaqItem(questionRes = R.string.faq_q7, answerRes = R.string.faq_a7),
                FaqItem(questionRes = R.string.faq_q8, answerRes = R.string.faq_a8),
                FaqItem(questionRes = R.string.faq_q9, answerRes = R.string.faq_a9),
                FaqItem(questionRes = R.string.faq_q10, answerRes = R.string.faq_a10)
        )

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FaqScreen() {
    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    Scaffold(
            modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_faq_title),
                        scrollBehavior = scrollBehavior
                )
            }
    ) { innerPadding ->
        Column(
                modifier =
                        Modifier.fillMaxSize()
                                .padding(innerPadding)
                                .verticalScroll(rememberScrollState())
                                .padding(horizontal = 16.dp, vertical = 8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Text(
                    text = stringResource(R.string.faq_header),
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.padding(bottom = 8.dp)
            )

            faqItems.forEach { item -> FaqCard(item) }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun FaqCard(item: FaqItem) {
    var expanded by remember { mutableStateOf(false) }
    val context = LocalContext.current

    // M3: Card(onClick) is the preferred M3 API over Card + clickable
    Card(
            onClick = { expanded = !expanded },
            modifier = Modifier.fillMaxWidth(),
            colors =
                    CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainer
                    )
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                        text = stringResource(item.questionRes),
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onSurface,
                        modifier = Modifier.weight(1f)
                )
                Icon(
                        imageVector =
                                if (expanded) Icons.Default.ExpandLess
                                else Icons.Default.ExpandMore,
                        contentDescription =
                                if (expanded) stringResource(R.string.cd_collapse)
                                else stringResource(R.string.cd_expand),
                        modifier = Modifier.size(24.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            AnimatedVisibility(
                    visible = expanded,
                    enter =
                            expandVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeIn(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    ),
                    exit =
                            shrinkVertically(
                                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                            ) +
                                    fadeOut(
                                            animationSpec =
                                                    MaterialTheme.motionScheme.fastEffectsSpec()
                                    )
            ) {
                Column {
                    HorizontalDivider(
                            modifier = Modifier.padding(vertical = 12.dp),
                            color = MaterialTheme.colorScheme.outlineVariant
                    )
                    Text(
                            text = stringResource(item.answerRes),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                    )

                    if (item.releasesLink) {
                        Spacer(modifier = Modifier.height(12.dp))
                        OutlinedButton(
                                onClick = {
                                    val intent =
                                            Intent(
                                                    Intent.ACTION_VIEW,
                                                    Uri.parse(
                                                            "https://github.com/clindsay94/RemEx/releases"
                                                    )
                                            )
                                    context.startActivity(intent)
                                }
                        ) { Text(stringResource(R.string.faq_github_button)) }
                    }
                }
            }
        }
    }
}
