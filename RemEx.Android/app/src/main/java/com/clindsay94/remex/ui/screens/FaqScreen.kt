package com.clindsay94.remex.ui.screens

import android.content.Intent
import android.net.Uri
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.clickable
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
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp

private data class FaqItem(
    val question: String,
    val answer: String,
    val releasesLink: Boolean = false
)

private val faqItems = listOf(
    FaqItem(
        question = "What do I need to run on my PC?",
        answer = "You need either Remex.Client.Desktop or Remex.Host running on " +
                "your PC.\n\n" +
                "• Remex.Client.Desktop — The full desktop app that bundles the " +
                "host automatically. Just launch it and the host starts in the " +
                "background on port 5005.\n\n" +
                "• Remex.Host — A standalone headless service, ideal for servers " +
                "or background-only use.\n\n" +
                "Both are self-contained (all runtimes included) — just extract " +
                "the publish folder and run. Download from the GitHub releases page.",
        releasesLink = true
    ),
    FaqItem(
        question = "How do I find my PC's IP address?",
        answer = "Windows:\n" +
                "Open Command Prompt and type 'ipconfig'. Look for the 'IPv4 " +
                "Address' line under your active network adapter (e.g. 192.168.1.42).\n\n" +
                "macOS:\n" +
                "Go to System Settings → Network and look at the connected interface, " +
                "or open Terminal and run: ipconfig getifaddr en0\n\n" +
                "Linux:\n" +
                "Open a terminal and run: ip addr  or  hostname -I\n\n" +
                "You can also see the IP directly in the Remex.Client.Desktop window."
    ),
    FaqItem(
        question = "Auto-discovery isn't finding my PC. What should I check?",
        answer = "1. Make sure Remex.Client.Desktop or Remex.Host is actually running " +
                "on your PC.\n" +
                "2. Both devices must be on the same Wi-Fi / LAN network.\n" +
                "3. On Android 13+, the NEARBY_WIFI_DEVICES permission must be granted. " +
                "RemEx will prompt for this when you tap 'Discover Automatically'.\n" +
                "4. Some routers have 'AP Isolation' or 'Client Isolation' enabled, " +
                "which blocks device-to-device communication. Check your router settings.\n" +
                "5. Corporate or guest Wi-Fi networks often block mDNS traffic. " +
                "Try using a personal or home network instead."
    ),
    FaqItem(
        question = "What is the default port?",
        answer = "The default port is 5005. If Remex.Client.Desktop finds port 5005 " +
                "in use, it will fall back to 5006. You can see the active port in " +
                "the desktop client's window.\n\n" +
                "If you changed the port on the host side, make sure to enter the " +
                "matching port in the Connection screen on this app."
    ),
    FaqItem(
        question = "What is the Access Key for?",
        answer = "The Access Key is an optional authentication token. If you set an " +
                "access key on the host (in Remex.Host settings), you must enter the " +
                "same key here to connect.\n\n" +
                "If you haven't configured one on the host, leave this field empty."
    ),
    FaqItem(
        question = "Can I connect over the internet (not just LAN)?",
        answer = "RemEx is designed for local network (LAN) use. To connect over the " +
                "internet you would need to set up port forwarding on your router for " +
                "port 5005 (or whichever port you use) and use your public IP address.\n\n" +
                "Important: Exposing the port to the internet is a security risk. " +
                "If you do this, always enable the Access Key for authentication. " +
                "A VPN is the recommended approach for remote access."
    ),
    FaqItem(
        question = "Remote Desktop is laggy or not working. What can I do?",
        answer = "• Lower the Quality slider in Connection → Remote Desktop Defaults.\n" +
                "• Reduce the Target FPS (15–20 FPS is sufficient for most use cases).\n" +
                "• Lower the Scale to reduce the resolution being streamed.\n" +
                "• Make sure your PC supports the screen capture backend: DXGI on " +
                "Windows, X11/Wayland on Linux.\n" +
                "• Check that you're on a fast, local Wi-Fi network (5 GHz preferred)."
    ),
    FaqItem(
        question = "What is Wake-on-LAN (WOL)?",
        answer = "Wake-on-LAN lets you power on your PC remotely by sending a " +
                "\"magic packet\" over the network.\n\n" +
                "To use it:\n" +
                "1. Enable WOL in your PC's BIOS/UEFI settings.\n" +
                "2. Enable WOL in your network adapter's properties (Windows: " +
                "Device Manager → Network Adapter → Power Management).\n" +
                "3. Enter your PC's MAC address in the Connection screen.\n" +
                "4. Use the 'Wake PC' card on the dashboard or the Remote Control screen."
    ),
    FaqItem(
        question = "How do I customize the app's appearance?",
        answer = "Go to the Personalization screen (tap the gear FAB on the dashboard " +
                "or navigate via the overflow menu → Settings).\n\n" +
                "You can change:\n" +
                "• Theme palette and color seed\n" +
                "• Card corner radius and shape presets\n" +
                "• Font family and scale\n" +
                "• Card opacity\n" +
                "• Dark/light/system theme mode"
    ),
    FaqItem(
        question = "Can I re-watch the tutorial?",
        answer = "Yes! Go to Settings from the overflow menu (⋮ → Settings). " +
                "There you'll find an option to replay the tutorial at any time."
    )
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FaqScreen() {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("FAQ", fontWeight = FontWeight.Bold) }
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Text(
                text = "Frequently Asked Questions",
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.padding(bottom = 8.dp)
            )

            faqItems.forEach { item ->
                FaqCard(item)
            }
        }
    }
}

@Composable
private fun FaqCard(item: FaqItem) {
    var expanded by remember { mutableStateOf(false) }
    val context = LocalContext.current

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable { expanded = !expanded },
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceVariant
        )
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = item.question,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.weight(1f)
                )
                Icon(
                    imageVector = if (expanded) Icons.Default.ExpandLess else Icons.Default.ExpandMore,
                    contentDescription = if (expanded) "Collapse" else "Expand",
                    modifier = Modifier.size(24.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }

            AnimatedVisibility(
                visible = expanded,
                enter = expandVertically(),
                exit = shrinkVertically()
            ) {
                Column {
                    HorizontalDivider(
                        modifier = Modifier.padding(vertical = 12.dp),
                        color = MaterialTheme.colorScheme.outlineVariant
                    )
                    Text(
                        text = item.answer,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )

                    if (item.releasesLink) {
                        Spacer(modifier = Modifier.height(12.dp))
                        OutlinedButton(
                            onClick = {
                                val intent = Intent(
                                    Intent.ACTION_VIEW,
                                    Uri.parse("https://github.com/clindsay94/RemEx/releases")
                                )
                                context.startActivity(intent)
                            }
                        ) {
                            Text("Open GitHub Releases")
                        }
                    }
                }
            }
        }
    }
}
