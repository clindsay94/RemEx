package com.clindsay94.remex.ui.screens

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.animation.AnimatedVisibility
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
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.clindsay94.remex.R
import com.clindsay94.remex.security.PinnedHostStore
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import java.util.concurrent.Executors
import kotlinx.coroutines.launch
import org.json.JSONObject

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QrScannerScreen(onScanned: (host: String, port: Int) -> Unit, onBack: () -> Unit) {
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

    // Re-check permission on resume (in case user went to settings)
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

    Scaffold(
            topBar = {
                TopAppBar(
                        title = { Text(stringResource(R.string.qr_scanner_title)) },
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
        Box(modifier = Modifier.fillMaxSize().padding(padding)) {
            if (hasCameraPermission) {
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
                                                Preview.Builder().build().also {
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
                                                                        val spkiHash =
                                                                                json.getString(
                                                                                        "spkiHashBase64"
                                                                                )

                                                                        if (!scannedOnce.value) {
                                                                            scannedOnce.value = true
                                                                            scope.launch {
                                                                                PinnedHostStore
                                                                                        .setPin(
                                                                                                context,
                                                                                                host,
                                                                                                spkiHash
                                                                                        )
                                                                            }
                                                                            onScanned(host, port)
                                                                        }
                                                                    } else if (json.has("accessKey")
                                                                    ) {
                                                                        errorMessage =
                                                                                context.getString(
                                                                                        R.string
                                                                                                .qr_error_old_format
                                                                                )
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

                // Error message overlay
                AnimatedVisibility(
                        visible = errorMessage != null,
                        modifier = Modifier.align(Alignment.Center)
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
                            color =
                                    MaterialTheme.colorScheme.surfaceContainerHighest.copy(
                                            alpha = 0.85f
                                    ),
                            modifier = Modifier.padding(horizontal = 32.dp)
                    ) {
                        Text(
                                text = stringResource(R.string.qr_scanner_hint),
                                style = MaterialTheme.typography.bodyMedium,
                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 10.dp)
                        )
                    }
                }
            } else {
                // Permission denied / not yet granted
                Column(
                        modifier = Modifier.fillMaxSize(),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                ) {
                    Icon(
                            Icons.Default.CameraAlt,
                            contentDescription = stringResource(R.string.cd_camera_icon),
                            modifier = Modifier.size(64.dp),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(Modifier.height(16.dp))
                    Text(
                            stringResource(R.string.qr_scanner_permission_required),
                            style = MaterialTheme.typography.bodyLarge
                    )
                    Spacer(Modifier.height(16.dp))
                    Button(onClick = { permissionLauncher.launch(Manifest.permission.CAMERA) }) {
                        Text(stringResource(R.string.qr_scanner_grant_permission))
                    }
                }
            }
        }
    }
}
