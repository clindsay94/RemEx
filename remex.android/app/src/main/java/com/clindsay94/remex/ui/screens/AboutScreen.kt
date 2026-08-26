package com.clindsay94.remex.ui.screens

import android.content.Intent
import android.net.Uri
import android.view.HapticFeedbackConstants
import androidx.compose.foundation.Image
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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.NewReleases
import androidx.compose.material.icons.filled.Smartphone
import androidx.compose.material.icons.filled.Terminal
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.theme.remexIconSquircle
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import org.json.JSONObject

@Composable
fun AboutScreen() {
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val hostCapabilities by RemexClientManager.hostCapabilities.collectAsStateWithLifecycle(initialValue = "")

    AboutScreenContent(
        isConnected = isConnected,
        hostCapabilities = hostCapabilities
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AboutScreenContent(
    isConnected: Boolean,
    hostCapabilities: String
) {
    val context = LocalContext.current
    val view = LocalView.current
    val connectedLabel = stringResource(R.string.status_connected)
    val disconnectedLabel = stringResource(R.string.status_disconnected)
    val unknownLabel = stringResource(R.string.about_status_unknown)

    val pcInfo =
            try {
                if (isConnected && hostCapabilities.isNotEmpty()) {
                    val json = JSONObject(hostCapabilities)
                    val version = json.optString("version", "")
                    val platform = json.optString("platform", "")
                    val runtime = json.optString("runtimeMode", "")

                    if (version.isNotEmpty()) {
                        version
                    } else if (platform.isNotEmpty() || runtime.isNotEmpty()) {
                        "$platform ($runtime)"
                    } else {
                        connectedLabel
                    }
                } else if (isConnected) {
                    connectedLabel
                } else {
                    disconnectedLabel
                }
            } catch (e: Exception) {
                unknownLabel
            }

    // The PC's build id in its comparable form (RemEx-d9guj). Separate from pcInfo because it
    // renders as the row's supporting line, mirroring how this phone's own row shows its id one
    // divider up — which is the entire point: identical shas one above the other mean identical
    // source, and a mismatch is visible without walking to the other device.
    val hostBuildId =
            try {
                if (isConnected && hostCapabilities.isNotEmpty()) {
                    remoteBuildIdLabel(JSONObject(hostCapabilities).optString("buildId", ""))
                } else {
                    ""
                }
            } catch (e: Exception) {
                ""
            }

    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    Scaffold(
            modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_about_title),
                        scrollBehavior = scrollBehavior
                )
            }
    ) { innerPadding ->
        Column(
                modifier =
                        Modifier.fillMaxSize()
                                .padding(innerPadding)
                                .verticalScroll(rememberScrollState())
                                .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally
        ) {
            // Squircle logo badge — matches the M3 Expressive launcher icon shape.
            Box(
                    modifier =
                            Modifier.padding(top = 8.dp)
                                    .size(144.dp)
                                    .clip(remexIconSquircle)
                                    .background(MaterialTheme.colorScheme.primaryContainer),
                    contentAlignment = Alignment.Center
            ) {
                Image(
                        painter = painterResource(R.drawable.ic_launcher_foreground_vector),
                        contentDescription = null,
                        modifier = Modifier.size(144.dp)
                )
            }

            Text(
                    text = stringResource(R.string.splash_tagline),
                    style = MaterialTheme.typography.titleMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
            )

            // Version Info Card
            Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                            )
            ) {
                Column(modifier = Modifier.padding(vertical = 8.dp)) {
                    // M3: ListItem for structured icon + label + value rows
                    ListItem(
                            leadingContent = {
                                Icon(
                                        imageVector = Icons.Default.Smartphone,
                                        contentDescription = stringResource(R.string.cd_phone),
                                        tint = MaterialTheme.colorScheme.primary
                                )
                            },
                            trailingContent = {
                                Text(
                                        BuildConfig.VERSION_NAME,
                                        style = MaterialTheme.typography.bodyMediumEmphasized
                                )
                            },
                            supportingContent =
                                    // Which BUILD, as opposed to which release (RemEx-f2x9g). The
                                    // "+" marker means the APK was built from uncommitted work, so
                                    // the commit it names is where the build started rather than
                                    // what it contains. Compare the sha half against the PC's — the
                                    // four characters after "+" are computed per platform.
                                    //
                                    // Absent, not "unknown", when git was unavailable at build
                                    // time: that is a fact about the build machine, and a row
                                    // saying "unknown" invites the reader to conclude something
                                    // about the APK instead.
                                    if (BuildConfig.BUILD_ID.isNotEmpty() &&
                                                    BuildConfig.BUILD_ID != "unknown"
                                    ) {
                                        {
                                            Text(
                                                    stringResource(
                                                            R.string.about_build_id,
                                                            BuildConfig.BUILD_ID
                                                    ),
                                                    style = MaterialTheme.typography.bodySmall,
                                                    fontFamily = FontFamily.Monospace
                                            )
                                        }
                                    } else null,
                            colors = ListItemDefaults.colors(containerColor = Color.Transparent)
                    ) {
                        Text(stringResource(R.string.about_android_client_label))
                    }
                    HorizontalDivider(
                            modifier = Modifier.padding(horizontal = 16.dp),
                            color = MaterialTheme.colorScheme.outlineVariant
                    )
                    ListItem(
                            leadingContent = {
                                Icon(
                                        imageVector = Icons.Default.Terminal,
                                        contentDescription = stringResource(R.string.cd_terminal),
                                        tint = MaterialTheme.colorScheme.secondary
                                )
                            },
                            trailingContent = {
                                Text(
                                        pcInfo,
                                        style = MaterialTheme.typography.bodyMediumEmphasized
                                )
                            },
                            supportingContent =
                                    // Same treatment as this phone's own id one divider up, so the
                                    // two shas sit adjacent and comparable. Only the sha half is
                                    // shown for the PC — its dirty suffix is hashed by a different
                                    // tool, so rendering it would invite a comparison that reports
                                    // a difference that is not there; the bare '+' keeps the one
                                    // thing the marker promises. Absent (old host) shows nothing,
                                    // never "unknown" (RemEx-d9guj).
                                    if (hostBuildId.isNotEmpty()) {
                                        {
                                            Text(
                                                    stringResource(
                                                            R.string.about_build_id,
                                                            hostBuildId
                                                    ),
                                                    style = MaterialTheme.typography.bodySmall,
                                                    fontFamily = FontFamily.Monospace
                                            )
                                        }
                                    } else null,
                            colors = ListItemDefaults.colors(containerColor = Color.Transparent)
                    ) {
                        Text(stringResource(R.string.about_pc_host_label))
                    }
                }
            }

            // What's New Section
            Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainerLow
                    )
            ) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Icon(
                                imageVector = Icons.Default.NewReleases,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.primary
                        )
                        Text(
                                text = stringResource(R.string.about_whats_new_title, BuildConfig.VERSION_NAME),
                                style = MaterialTheme.typography.titleMediumEmphasized
                        )
                    }
                    WhatsNewEntry(
                            version = stringResource(R.string.about_whats_new_1_label),
                            body = stringResource(R.string.about_whats_new_1_body)
                    )
                    HorizontalDivider(modifier = Modifier.padding(horizontal = 4.dp), color = MaterialTheme.colorScheme.outlineVariant)
                    WhatsNewEntry(
                            version = stringResource(R.string.about_whats_new_2_label),
                            body = stringResource(R.string.about_whats_new_2_body)
                    )
                    HorizontalDivider(modifier = Modifier.padding(horizontal = 4.dp), color = MaterialTheme.colorScheme.outlineVariant)
                    WhatsNewEntry(
                            version = stringResource(R.string.about_whats_new_3_label),
                            body = stringResource(R.string.about_whats_new_3_body)
                    )
                    HorizontalDivider(modifier = Modifier.padding(horizontal = 4.dp), color = MaterialTheme.colorScheme.outlineVariant)
                    WhatsNewEntry(
                            version = stringResource(R.string.about_whats_new_4_label),
                            body = stringResource(R.string.about_whats_new_4_body)
                    )
                    HorizontalDivider(modifier = Modifier.padding(horizontal = 4.dp), color = MaterialTheme.colorScheme.outlineVariant)
                    WhatsNewEntry(
                            version = stringResource(R.string.about_whats_new_5_label),
                            body = stringResource(R.string.about_whats_new_5_body)
                    )
                }
            }

            // GitHub Section
            Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.secondaryContainer
                            )
            ) {
                Column(
                        modifier = Modifier.padding(16.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text(
                            text = stringResource(R.string.about_github_star),
                            style = MaterialTheme.typography.bodyLargeEmphasized
                    )
                    Text(
                            text = stringResource(R.string.about_github_cat),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSecondaryContainer
                    )

                    Button(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                val intent =
                                        Intent(
                                                Intent.ACTION_VIEW,
                                                Uri.parse("https://github.com/clindsay94/remex")
                                        )
                                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                                try {
                                    context.startActivity(intent)
                                } catch (e: Exception) {
                                    // Fallback or log if browser not found
                                }
                            },
                            modifier = Modifier.fillMaxWidth()
                    ) {
                        Icon(Icons.Default.Code, contentDescription = null)
                        Spacer(modifier = Modifier.width(8.dp))
                        Text(stringResource(R.string.about_view_github))
                    }
                }
            }

            Spacer(modifier = Modifier.weight(1f))

            // Footer
            Text(
                    text = stringResource(R.string.about_made_with),
                    style = MaterialTheme.typography.labelLargeEmphasized,
                    color = MaterialTheme.colorScheme.primary
            )

            Spacer(modifier = Modifier.height(24.dp))
        }
    }
}

@Composable
private fun WhatsNewEntry(version: String, body: String) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(
                text = version,
                style = MaterialTheme.typography.labelLargeEmphasized,
                color = MaterialTheme.colorScheme.primary
        )
        Text(
                text = body,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun AboutScreenPreview() {
    RemExTheme {
        AboutScreenContent(
            isConnected = true,
            hostCapabilities = "{\"version\":\"1.2.3\",\"platform\":\"Windows\",\"runtimeMode\":\"Native\"}"
        )
    }
}

/**
 * Reduces a remote build id to its comparable form: the sha, plus a bare '+' when the remote build
 * was dirty — "39b0b09+a3f1" becomes "39b0b09+" (RemEx-d9guj).
 *
 * The characters after '+' are hashed independently by Gradle and MSBuild, so two dirty builds of
 * the SAME tree carry different suffixes; showing the remote suffix beside the local one invites a
 * comparison that reports a difference that is not there. Empty and the wire sentinel "unknown"
 * both map to empty, so an old host shows nothing rather than "unknown" — the rule both About
 * screens already follow for their own ids. Mirrors AppVersion.NormalizeRemoteBuildId on the PC.
 */
internal fun remoteBuildIdLabel(raw: String): String {
    val trimmed = raw.trim()
    if (trimmed.isEmpty() || trimmed.equals("unknown", ignoreCase = true)) return ""
    val plus = trimmed.indexOf('+')
    if (plus < 0) return trimmed
    val sha = trimmed.substring(0, plus).trim()
    return if (sha.isEmpty()) "" else "$sha+"
}
