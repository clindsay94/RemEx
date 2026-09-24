package com.clindsay94.remex.ui.screens

/**
 * When the `/ws/desktop` socket may be closed because only a side request opened it (perf audit P0-7).
 *
 * The display-catalog preload (and a window query or action) reaches the host over the SAME socket
 * the stream uses: `RemexDesktopClient.RequestDisplayCatalogAsync` connects it if it is down, and
 * nothing then closed it again. So a phone that merely connected to its PC kept a second TLS socket
 * open for as long as the app lived, with no stream on it. The close is `StopDesktopStream`, which
 * the core already runs as stop + disconnect and which a display-switch restart already relies on
 * being immediately followed by a start.
 *
 * **NEVER WHILE A STREAM OWNS THE SOCKET, OR IS ABOUT TO.** The catalog's reply is what triggers a
 * waiting DesktopStart, so a close that landed AFTER that start would tear down the stream it just
 * brought up — the silent-black-stream class (frames never arrive, nothing errors). The core runs
 * desktop work on one ordered queue, so a close enqueued BEFORE a start is harmless (the start
 * reconnects); both are enqueued from the main thread, so reading these flags there and refusing
 * whenever a start is running, pending on the catalog, or scheduled (reconnect backoff, display
 * switch restart) is what keeps the close ahead of every start rather than behind one.
 *
 * Plain Kotlin so the decision is unit-testable.
 */
internal object DesktopSocketIdlePolicy {

    /** How long a stream-less desktop socket is kept after its last side request (30-60 s per P0-7). */
    const val IDLE_CLOSE_MS = 45_000L

    /**
     * @param socketMayBeOpen a side request opened the socket and nothing has closed it since.
     * @param isStreaming a stream is running and owns the socket.
     * @param pendingStreamStart a start is waiting on the display catalog.
     * @param startScheduled a reconnect backoff or display-switch restart will start a stream.
     */
    fun mayClose(
        socketMayBeOpen: Boolean,
        isStreaming: Boolean,
        pendingStreamStart: Boolean,
        startScheduled: Boolean,
    ): Boolean = socketMayBeOpen && !isStreaming && !pendingStreamStart && !startScheduled
}
