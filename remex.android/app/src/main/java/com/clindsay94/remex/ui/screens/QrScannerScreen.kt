package com.clindsay94.remex.ui.screens

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ExperimentalGetImage
import androidx.camera.core.ImageAnalysis
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CameraAlt
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.viewinterop.AndroidView
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.security.PinnedHostStore
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import java.util.concurrent.Executors
import kotlinx.coroutines.launch
import org.json.JSONObject

@OptIn(ExperimentalMaterial3Api::class)
// A SECOND ANNOTATION RATHER THAN A SECOND ARGUMENT ABOVE, AND NOT BY PREFERENCE (RemEx-28wng).
// ExperimentalGetImage is declared in Java against androidx.annotation.RequiresOptIn, and Kotlin's
// built-in @OptIn accepts only Kotlin-declared markers, so the two cannot share a line. Fully
// qualified because importing androidx.annotation.OptIn would collide with the kotlin.OptIn that is
// imported by default and in use directly above.
//
// It covers imageProxy.image in the ImageAnalysis analyzer below. CameraX marks that getter to warn
// callers the signature may move under them; naming the marker here records that the risk was seen
// and accepted at the one call site that takes it, rather than leaving a lint error standing that
// would bury the next genuinely unsafe opt-in in a list that already has one in it.
@androidx.annotation.OptIn(markerClass = [ExperimentalGetImage::class])
@Composable
fun QrScannerScreen(onScanned: (host: String, port: Int, pin: String) -> Unit, onBack: () -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()
    val scannedOnce = remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    var hasCameraPermission by remember {
        mutableStateOf(
                ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                        PackageManager.PERMISSION_GRANTED
        )
    }

    val permissionLauncher =
            rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted
                ->
                hasCameraPermission = granted
            }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                hasCameraPermission =
                        ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                                PackageManager.PERMISSION_GRANTED
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    LaunchedEffect(hasCameraPermission) {
        if (!hasCameraPermission) {
            permissionLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    val barcodeScanner = remember {
        BarcodeScanning.getClient(
                BarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
        )
    }

    val analysisExecutor = remember { Executors.newSingleThreadExecutor() }
    val cameraProviderFuture = remember { ProcessCameraProvider.getInstance(context) }

    DisposableEffect(Unit) {
        onDispose {
            analysisExecutor.shutdown()
            barcodeScanner.close()
        }
    }

    QrScannerScreenContent(
        hasCameraPermission = hasCameraPermission,
        errorMessage = errorMessage,
        onBack = onBack,
        onGrantPermission = { permissionLauncher.launch(Manifest.permission.CAMERA) },
        cameraView = {
            AndroidView(
                    factory = { ctx ->
                        val previewView =
                                PreviewView(ctx).apply {
                                    implementationMode =
                                            PreviewView.ImplementationMode.COMPATIBLE
                                    scaleType = PreviewView.ScaleType.FILL_CENTER
                                }

                        cameraProviderFuture.addListener(
                                {
                                    val cameraProvider =
                                            try {
                                                cameraProviderFuture.get()
                                            } catch (e: Exception) {
                                                android.util.Log.e(
                                                        "QrScanner",
                                                        "Camera provider init failed",
                                                        e
                                                )
                                                return@addListener
                                            }

                                    val preview =
                                            androidx.camera.core.Preview.Builder().build().also {
                                                it.setSurfaceProvider(
                                                        previewView.surfaceProvider
                                                )
                                            }

                                    val imageAnalysis =
                                            ImageAnalysis.Builder()
                                                    .setBackpressureStrategy(
                                                            ImageAnalysis
                                                                    .STRATEGY_KEEP_ONLY_LATEST
                                                    )
                                                    .build()

                                    imageAnalysis.setAnalyzer(analysisExecutor) { imageProxy ->
                                        if (scannedOnce.value) {
                                            imageProxy.close()
                                            return@setAnalyzer
                                        }

                                        val mediaImage = imageProxy.image
                                        if (mediaImage != null) {
                                            val image =
                                                    InputImage.fromMediaImage(
                                                            mediaImage,
                                                            imageProxy.imageInfo.rotationDegrees
                                                    )
                                            barcodeScanner
                                                    .process(image)
                                                    .addOnSuccessListener { barcodes ->
                                                        for (barcode in barcodes) {
                                                            val raw =
                                                                    barcode.rawValue ?: continue
                                                            try {
                                                                val json = JSONObject(raw)
                                                                if (json.has("spkiHashBase64")
                                                                ) {
                                                                    val host =
                                                                            json.getString(
                                                                                    "host"
                                                                            )
                                                                    val port =
                                                                            json.getInt("port")
                                                                    val hostId =
                                                                            json.optString(
                                                                                            "hostId"
                                                                                    )
                                                                                    .takeIf {
                                                                                        it.isNotBlank()
                                                                                    }
                                                                    val spkiHash =
                                                                            json.getString(
                                                                                    "spkiHashBase64"
                                                                            )
                                                                    val pin =
                                                                            json.optString(
                                                                                    "pin",
                                                                                    ""
                                                                            )

                                                                    if (!scannedOnce.value) {
                                                                        scannedOnce.value = true
                                                                        scope.launch {
                                                                            try {
                                                                                 if (spkiHash.isBlank()
                                                                                 ) {
                                                                                     errorMessage = context.getString(R.string.qr_error_missing_hash)
                                                                                     scannedOnce.value = false
                                                                                     return@launch
                                                                                 }

                                                                                 if (!hostId.isNullOrBlank()
                                                                                 ) {
                                                                                     RemexCoreClient
                                                                                             .SetPinnedHostHash(
                                                                                                     hostId,
                                                                                                     spkiHash
                                                                                             )
                                                                                     PinnedHostStore
                                                                                             .setPin(
                                                                                                     context,
                                                                                                     hostId,
                                                                                                     spkiHash
                                                                                             )
                                                                                 }

                                                                                 RemexCoreClient
                                                                                         .SetPinnedHostHash(
                                                                                                 host,
                                                                                                 spkiHash
                                                                                         )
                                                                                 PinnedHostStore
                                                                                         .setPin(
                                                                                                 context,
                                                                                                 host,
                                                                                                 spkiHash
                                                                                         )
                                                                                 // Link the keys so
                                                                                 // a later forget
                                                                                 // clears them all
                                                                                 // (RemEx-uxem).
                                                                                 PinnedHostStore
                                                                                         .recordAliases(
                                                                                                 context,
                                                                                                 listOfNotNull(
                                                                                                         hostId,
                                                                                                         host,
                                                                                                         spkiHash
                                                                                                 )
                                                                                         )
                                                                                 errorMessage =
                                                                                         null
                                                                                 onScanned(
                                                                                         host,
                                                                                         port,
                                                                                         pin
                                                                                 )
                                                                             } catch (
                                                                                     e:
                                                                                             Exception) {
                                                                                 // Never surface
                                                                                 // raw exception
                                                                                 // text to users;
                                                                                 // log it instead.
                                                                                 android.util.Log
                                                                                         .e(
                                                                                                 "QrScanner",
                                                                                                 "QR pairing setup failed",
                                                                                                 e
                                                                                         )
                                                                                 errorMessage =
                                                                                         context.getString(R.string.qr_error_setup_failed)
                                                                                 scannedOnce
                                                                                         .value =
                                                                                         false
                                                                             }
                                                                        }
                                                                    }
                                                                }
                                                            } catch (_: Exception) {
                                                                // Not a RemEx QR code — keep
                                                                // scanning
                                                            }
                                                        }
                                                    }
                                                    .addOnCompleteListener {
                                                        imageProxy.close()
                                                    }
                                        } else {
                                            imageProxy.close()
                                        }
                                    }

                                    try {
                                        cameraProvider.unbindAll()
                                        cameraProvider.bindToLifecycle(
                                                lifecycleOwner,
                                                CameraSelector.DEFAULT_BACK_CAMERA,
                                                preview,
                                                imageAnalysis
                                        )
                                    } catch (e: Exception) {
                                        android.util.Log.e(
                                                "QrScanner",
                                                "Camera binding failed",
                                                e
                                        )
                                    }
                                },
                                ContextCompat.getMainExecutor(context)
                        )

                        previewView
                    },
                    modifier = Modifier.fillMaxSize()
            )
        }
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QrScannerScreenContent(
    hasCameraPermission: Boolean,
    errorMessage: String?,
    onBack: () -> Unit,
    onGrantPermission: () -> Unit,
    cameraView: @Composable () -> Unit
) {
    Scaffold(
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.qr_scanner_title),
                        navigationIcon = {
                            IconButton(onClick = onBack) {
                                Icon(
                                        Icons.AutoMirrored.Filled.ArrowBack,
                                        contentDescription = stringResource(R.string.cd_back)
                                )
                            }
                        }
                )
            }
    ) { padding ->
        // Permission rationale and camera preview cross-fade instead of hard-swapping the
        // moment the permission dialog is dismissed (this was the file's one bare body swap).
        val permissionFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
        AnimatedContent(
                targetState = hasCameraPermission,
                transitionSpec = {
                    fadeIn(permissionFadeSpec) togetherWith fadeOut(permissionFadeSpec)
                },
                modifier = Modifier.fillMaxSize().padding(padding),
                label = "qrPermissionGate"
        ) { cameraGranted ->
            if (cameraGranted) {
                QrCameraContent(errorMessage = errorMessage, cameraView = cameraView)
            } else {
                QrPermissionRationale(onGrantPermission = onGrantPermission)
            }
        }
    }
}

@Composable
private fun QrCameraContent(errorMessage: String?, cameraView: @Composable () -> Unit) {
    Box(modifier = Modifier.fillMaxSize()) {
        cameraView()

        // Error message overlay
        AnimatedVisibility(
                visible = errorMessage != null,
                modifier = Modifier.align(Alignment.Center),
                enter =
                        expandVertically(
                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                        ) +
                                fadeIn(
                                        animationSpec = MaterialTheme.motionScheme.fastEffectsSpec()
                                ),
                exit =
                        shrinkVertically(
                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                        ) +
                                fadeOut(
                                        animationSpec = MaterialTheme.motionScheme.fastEffectsSpec()
                                ),
                label = "qrError"
        ) {
            Surface(
                    shape = MaterialTheme.shapes.medium,
                    color = MaterialTheme.colorScheme.errorContainer.copy(alpha = 0.9f),
                    modifier = Modifier.padding(32.dp)
            ) {
                Text(
                        text = errorMessage ?: "",
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        modifier = Modifier.padding(16.dp)
                )
            }
        }

        // Scanning hint overlay
        Box(
                modifier = Modifier.fillMaxSize().padding(bottom = 48.dp),
                contentAlignment = Alignment.BottomCenter
        ) {
            Surface(
                    shape = MaterialTheme.shapes.medium,
                    color = MaterialTheme.colorScheme.surfaceContainerHighest.copy(alpha = 0.85f),
                    modifier = Modifier.padding(horizontal = 32.dp)
            ) {
                Text(
                        text = stringResource(R.string.qr_scanner_hint),
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 10.dp)
                )
            }
        }
    }
}

@Composable
private fun QrPermissionRationale(onGrantPermission: () -> Unit) {
    // Permission denied / not yet granted
    Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
    ) {
        Icon(
                Icons.Default.CameraAlt,
                contentDescription = null /* decorative: the adjacent Text already says it (RemEx-xqli) */,
                modifier = Modifier.size(64.dp),
                tint = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(Modifier.height(16.dp))
        Text(
                stringResource(R.string.qr_scanner_permission_required),
                style = MaterialTheme.typography.bodyLarge
        )
        Spacer(Modifier.height(16.dp))
        Button(onClick = onGrantPermission) {
            Text(stringResource(R.string.qr_scanner_grant_permission))
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun QrScannerScreenPreview() {
    RemExTheme {
        QrScannerScreenContent(
            hasCameraPermission = true,
            errorMessage = null,
            onBack = {},
            onGrantPermission = {},
            cameraView = {
                // Design-time stand-in for the real AndroidView camera feed (QrScannerScreen.kt's
                // production cameraView lambda). Never shown to a user — @Preview composables are
                // stripped from release builds — so this text is intentionally not a stringResource.
                Box(
                    modifier = Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        "[Preview] Camera feed placeholder",
                        color = MaterialTheme.colorScheme.onSurface
                    )
                }
            }
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun QrScannerScreenPermissionPreview() {
    RemExTheme {
        QrScannerScreenContent(
            hasCameraPermission = false,
            errorMessage = null,
            onBack = {},
            onGrantPermission = {},
            cameraView = {}
        )
    }
}
