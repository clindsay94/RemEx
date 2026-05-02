package com.clindsay94.remex.ui.screens

import android.content.Intent
import android.net.Uri
import android.view.HapticFeedbackConstants
import androidx.compose.foundation.Image
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.layout.ContentScale
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Smartphone
import androidx.compose.material.icons.filled.Terminal
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.BuildConfig
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import org.json.JSONObject

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AboutScreen() {
    val context = LocalContext.current
    val view = LocalView.current
    val isConnected by RemexClientManager.isConnected.collectAsState()
    val hostCapabilities by RemexClientManager.hostCapabilities.collectAsState(initial = "")

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
                        "Connected"
                    }
                } else if (isConnected) {
                    "Connected"
                } else {
                    "Disconnected"
                }
            } catch (e: Exception) {
                "Unknown"
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
            // Wordmark
            Image(
                    painter = painterResource(
                            if (isSystemInDarkTheme()) R.drawable.remex_wordmark_dark
                            else R.drawable.remex_wordmark_light
                    ),
                    contentDescription = stringResource(R.string.app_name),
                    contentScale = ContentScale.Fit,
                    modifier = Modifier.fillMaxWidth(0.72f).padding(vertical = 16.dp)
            )

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
                            headlineContent = { Text("Android Client") },
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
                                        style = MaterialTheme.typography.bodyMedium,
                                        fontWeight = FontWeight.Bold
                                )
                            },
                            colors = ListItemDefaults.colors(containerColor = Color.Transparent)
                    )
                    HorizontalDivider(
                            modifier = Modifier.padding(horizontal = 16.dp),
                            color = MaterialTheme.colorScheme.outlineVariant
                    )
                    ListItem(
                            headlineContent = { Text("PC Host") },
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
                                        style = MaterialTheme.typography.bodyMedium,
                                        fontWeight = FontWeight.Bold
                                )
                            },
                            colors = ListItemDefaults.colors(containerColor = Color.Transparent)
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
                            style = MaterialTheme.typography.bodyLarge,
                            fontWeight = FontWeight.SemiBold
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
                        Text("View Source on GitHub")
                    }
                }
            }

            Spacer(modifier = Modifier.weight(1f))

            // Footer
            Text(
                    text = stringResource(R.string.about_made_with),
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.primary,
                    fontWeight = FontWeight.Bold
            )

            Spacer(modifier = Modifier.height(24.dp))
        }
    }
}
