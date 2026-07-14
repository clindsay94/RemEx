package com.clindsay94.remex.service

import android.util.Base64
import android.util.Log
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.CompletableDeferred
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString

/** A registered receiver of decoded binary frames for one transferId on the shared `/ws/files` socket. */
interface FileFrameSink {
    fun onFrame(envelope: FileFrameEnvelope, payload: ByteArray)

    /** The channel dropped; the sink should suspend/pause and (if it is a receiver) keep its partial. */
    fun onChannelClosed()
}

/** The binary data plane the engine and host mirror both drive. Backed by [FileTransferChannelClient]. */
interface FileFrameChannel {
    val isOpen: Boolean

    fun registerSink(transferId: String, sink: FileFrameSink)

    fun unregisterSink(transferId: String)

    /** Sends one frame. Returns false if the socket is not open. */
    fun sendFrame(envelope: FileFrameEnvelope, payload: ByteArray): Boolean
}

/**
 * OkHttp WSS client for the dedicated binary `/ws/files` channel (plan §1.1, WP5). This is a plain
 * Kotlin socket — **no JNI / native-lib changes** — that carries only bulk `data`/`ack`/`error`
 * frames; the JSON control plane (offer/ready/complete/result/control/browse/consent) stays on the
 * native `/ws` socket via `RemexCoreClient`.
 *
 * Security mirrors the pairing trust model: the host cert is **SPKI-pinned**. We install a custom
 * [X509TrustManager] that verifies the leaf certificate's SubjectPublicKeyInfo SHA-256 against the
 * hash pinned at pairing time (stored in `PinnedHostStore`), plus a hostname verifier that trusts the
 * SPKI pin rather than the CN/SAN (the host uses a self-signed cert reached by IP). This is the exact
 * shape of certificate pinning called for by the plan; it never weakens pairing/cert handling.
 *
 * The socket is shared: frames for a given transferId are dispatched to the [FileFrameSink]
 * registered under that id, so both Android-initiated transfers ([FileTransferEngine]) and
 * PC-initiated transfers served by [FileHostHandler] can multiplex over the one connection.
 */
object FileTransferChannelClient : FileFrameChannel {

    private const val TAG = "FileChannelClient"

    private val sinks = ConcurrentHashMap<String, FileFrameSink>()

    @Volatile private var webSocket: WebSocket? = null
    @Volatile private var openState = false
    @Volatile private var currentKey: String? = null

    override val isOpen: Boolean get() = openState

    override fun registerSink(transferId: String, sink: FileFrameSink) {
        sinks[transferId] = sink
    }

    override fun unregisterSink(transferId: String) {
        sinks.remove(transferId)
    }

    override fun sendFrame(envelope: FileFrameEnvelope, payload: ByteArray): Boolean {
        val ws = webSocket ?: return false
        if (!openState) return false
        val frame = FileFrameCodec.wrap(envelope, payload)
        return ws.send(frame.toByteString(0, frame.size))
    }

    /** Sends a data frame without an extra payload copy. */
    fun sendData(
        transferId: String,
        offset: Long,
        buffer: ByteArray,
        count: Int,
        final: Boolean,
    ): Boolean {
        val ws = webSocket ?: return false
        if (!openState) return false
        val env =
            FileFrameEnvelope(
                kind = FileFrameKinds.DATA,
                transferId = transferId,
                offset = offset,
                length = count,
                final = final,
            )
        val frame = FileFrameCodec.wrap(env, buffer, 0, count)
        return ws.send(frame.toByteString(0, frame.size))
    }

    fun sendAck(transferId: String, committedOffset: Long): Boolean =
        sendFrame(
            FileFrameEnvelope(
                kind = FileFrameKinds.ACK,
                transferId = transferId,
                committedOffset = committedOffset,
            ),
            EMPTY,
        )

    fun sendError(transferId: String, error: String): Boolean =
        sendFrame(
            FileFrameEnvelope(
                kind = FileFrameKinds.ERROR,
                transferId = transferId,
                error = error,
            ),
            EMPTY,
        )

    /**
     * Ensures the socket is connected to `wss://<host>:<port>/ws/files?clientId=..&protocolVersion=3`,
     * SPKI-pinned to [spkiHash]. Idempotent: returns immediately when already open to the same host.
     * Suspends until the WebSocket handshake completes (or fails). Returns true on success.
     */
    suspend fun ensureConnected(
        host: String,
        port: Int,
        clientId: String,
        spkiHash: String,
    ): Boolean {
        val key = "$host:$port:$clientId"
        if (openState && currentKey == key) return true
        // Tear down any prior socket to a different host before reconnecting.
        closeInternal()

        val trustManager =
            try {
                SpkiPinningTrustManager(spkiHash)
            } catch (e: Exception) {
                Log.e(TAG, "Invalid SPKI pin; refusing to connect", e)
                return false
            }
        val sslContext = SSLContext.getInstance("TLS")
        sslContext.init(null, arrayOf<javax.net.ssl.TrustManager>(trustManager), SecureRandom())

        val client =
            OkHttpClient.Builder()
                .sslSocketFactory(sslContext.socketFactory, trustManager)
                .hostnameVerifier { _, session ->
                    // Identity on this channel is the pinned SPKI, not a DNS name: the host cert is
                    // self-signed and the user may dial an IP, a MagicDNS name, or a LAN name — so a
                    // name check is meaningless, but an unconditional trust-all verifier is ASI-flagged
                    // and wrong in principle. Verify the presented leaf against the same SPKI pin the TLS
                    // handshake already enforced (belt-and-braces); anything else returns false.
                    try {
                        val leaf = session.peerCertificates.firstOrNull() as? X509Certificate
                        leaf != null && trustManager.matchesPin(leaf)
                    } catch (e: Exception) {
                        false
                    }
                }
                // Tighter ping so OkHttp detects a silently-dead peer (and fires onFailure -> reconnect)
                // in ~10-20s of background hygiene, instead of waiting on OS TCP retransmit (minutes). The
                // primary staleness defence is invalidate() on control-reconnect / transfer failure. (ix8d)
                .pingInterval(10, TimeUnit.SECONDS)
                .connectTimeout(15, TimeUnit.SECONDS)
                .readTimeout(0, TimeUnit.MILLISECONDS)
                .build()

        val url =
            "wss://$host:$port/ws/files?clientId=" +
                java.net.URLEncoder.encode(clientId, "UTF-8") +
                "&protocolVersion=3"
        val request = Request.Builder().url(url).build()

        val opened = CompletableDeferred<Boolean>()
        val listener =
            object : WebSocketListener() {
                override fun onOpen(ws: WebSocket, response: Response) {
                    openState = true
                    currentKey = key
                    Log.i(TAG, "Binary /ws/files channel open to $host:$port")
                    opened.complete(true)
                }

                override fun onMessage(ws: WebSocket, bytes: ByteString) {
                    dispatch(bytes.toByteArray())
                }

                override fun onClosing(ws: WebSocket, code: Int, reason: String) {
                    ws.close(code, null)
                }

                override fun onClosed(ws: WebSocket, code: Int, reason: String) {
                    handleClosed()
                }

                override fun onFailure(ws: WebSocket, t: Throwable, response: Response?) {
                    Log.w(TAG, "Binary /ws/files channel failure: ${t.message} (http=${response?.code})")
                    if (!opened.isCompleted) opened.complete(false)
                    handleClosed()
                }
            }

        webSocket = client.newWebSocket(request, listener)
        // Release the dispatcher threads once the socket is retired.
        client.dispatcher.executorService.shutdown()
        return try {
            opened.await()
        } catch (e: Exception) {
            false
        }
    }

    private fun dispatch(frameBytes: ByteArray) {
        val decoded = FileFrameCodec.tryRead(frameBytes) ?: run {
            Log.w(TAG, "Discarding malformed /ws/files frame (${frameBytes.size} bytes)")
            return
        }
        val sink = sinks[decoded.envelope.transferId]
        if (sink == null) {
            Log.w(TAG, "No sink for transfer ${decoded.envelope.transferId} (${decoded.envelope.kind})")
            return
        }
        sink.onFrame(decoded.envelope, decoded.payload)
    }

    private fun handleClosed() {
        openState = false
        currentKey = null
        webSocket = null
        // Notify every live sink so receivers keep partials and senders pause.
        sinks.values.toList().forEach {
            try {
                it.onChannelClosed()
            } catch (e: Exception) {
                Log.w(TAG, "sink.onChannelClosed threw", e)
            }
        }
    }

    private fun closeInternal() {
        try {
            webSocket?.close(1000, "reconnect")
        } catch (e: Exception) {
            // best-effort
        }
        webSocket = null
        openState = false
        currentKey = null
    }

    fun close() {
        closeInternal()
        sinks.clear()
    }

    /**
     * Force-nukes the channel socket with OkHttp [WebSocket.cancel] (NOT a graceful [WebSocket.close],
     * which waits for a close handshake that a silently-dead peer will never answer) and resets state via
     * [handleClosed] so the next [ensureConnected] dials a fresh socket instead of reusing a zombie.
     *
     * `ws.send()` only enqueues into OkHttp's transmit buffer and returns true even on a half-open/dead
     * TCP link, so after a silent drop (host restart, Wi-Fi blip, doze) the app streams bytes that never
     * leave the device — the host receives nothing and the transfer stalls. Call this the moment we know
     * the link is suspect: the control channel reconnected (both sockets target the same host, so the data
     * socket is almost certainly stale too) or a transfer timed out. (RemEx-ix8d.)
     */
    fun invalidate() {
        val stale = webSocket
        try {
            stale?.cancel()
        } catch (e: Exception) {
            // best-effort
        }
        handleClosed()
    }

    private val EMPTY = ByteArray(0)

    /**
     * X509TrustManager that accepts the host **iff** its leaf certificate's SPKI SHA-256 matches the
     * pin captured at pairing time. Normalizes an optional `sha256/` prefix so it accepts either the
     * raw base64 the native client stores or the OkHttp-style pin string.
     */
    private class SpkiPinningTrustManager(pin: String) : X509TrustManager {
        private val expected: String = normalize(pin)

        init {
            require(expected.isNotBlank()) { "Empty SPKI pin" }
        }

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            // Server-only channel; never asked to verify a client cert.
        }

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            val leaf =
                chain?.firstOrNull()
                    ?: throw CertificateException("No server certificate presented.")
            if (!matchesPin(leaf)) {
                throw CertificateException(
                    "Host SPKI pin mismatch on /ws/files — refusing connection."
                )
            }
        }

        /**
         * True iff [cert]'s SubjectPublicKeyInfo SHA-256 matches the pinned hash. Factored out of
         * checkServerTrusted so the hostname verifier can enforce the SAME pin — the peer identity
         * on this channel is the pinned SPKI, not a DNS name — replacing the ASI-flagged
         * unconditional trust-all verifier with a conditional one that can return false.
         */
        fun matchesPin(cert: X509Certificate): Boolean {
            val spki = MessageDigest.getInstance("SHA-256").digest(cert.publicKey.encoded)
            val actual = Base64.encodeToString(spki, Base64.NO_WRAP)
            return actual == expected
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

        private fun normalize(pin: String): String = pin.trim().removePrefix("sha256/").trim()
    }
}
