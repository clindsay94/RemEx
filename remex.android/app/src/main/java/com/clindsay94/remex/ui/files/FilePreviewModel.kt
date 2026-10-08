package com.clindsay94.remex.ui.files

import com.clindsay94.remex.service.FileTransferLimits
import com.clindsay94.remex.service.HashFormat
import com.clindsay94.remex.ui.files.preview.ImagePreviewLoader
import com.clindsay94.remex.ui.files.preview.PreviewClassifier
import com.clindsay94.remex.ui.files.preview.PreviewKind
import com.clindsay94.remex.ui.files.preview.PreviewTooLargeException
import com.clindsay94.remex.ui.files.preview.RangeReader
import com.clindsay94.remex.ui.files.preview.SyntaxLanguage
import com.clindsay94.remex.ui.files.preview.SyntaxSpan
import com.clindsay94.remex.ui.files.preview.SyntaxTokenizer
import com.clindsay94.remex.ui.files.preview.TextPreviewLoader
import com.clindsay94.remex.ui.files.preview.TextTail
import com.clindsay94.remex.ui.files.preview.TailUpdate
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** The file the preview is showing, on either device. */
data class PreviewTarget(
    val side: FileSide,
    val rootId: String,
    val path: String,
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
    val modifiedMs: Long,
)

/** One line of a text preview. [number] is null in a live tail, which starts somewhere in the middle. */
data class PreviewLine(val number: Int?, val text: String, val spans: List<SyntaxSpan>)

enum class PreviewState {
    Empty, Loading, Image, Text, Binary, TooLarge, Details, Failed,

    /** The device holding the file is too old to send previews: its details, and a word about updating. */
    NeedsUpdate,
}

enum class HashCompareOutcome { None, Match, Mismatch, NotAHash }

enum class CounterpartOutcome { None, Identical, Different, Failed }

/** Everything the preview pane draws. */
data class PreviewUi<I>(
    val target: PreviewTarget? = null,
    val state: PreviewState = PreviewState.Empty,
    val image: I? = null,
    val progress: Float = 0f,
    val lines: List<PreviewLine> = emptyList(),
    val pendingLine: String = "",
    val truncated: Boolean = false,
    val live: Boolean = false,
    val fileSize: Long = -1,
    val hashHex: String? = null,
    val hashing: Boolean = false,
    val hashFailed: Boolean = false,
    val compare: HashCompareOutcome = HashCompareOutcome.None,
    val counterpart: CounterpartOutcome = CounterpartOutcome.None,
    val verifying: Boolean = false,
    /** This file's device can work out a fingerprint. */
    val canHash: Boolean = true,
    /** Both devices can, so it can be checked against a copy on the other one. */
    val canVerifyAcross: Boolean = true,
) {
    val canTail: Boolean get() = state == PreviewState.Text
}

/** Where the preview's bytes and fingerprints come from: the phone's own files or the PC's. */
interface PreviewSources<I> {
    fun reader(target: PreviewTarget): RangeReader

    /** The file's SHA-256, Base64 as on the wire. Throws when it can't be computed or the PC refuses. */
    suspend fun hashBase64(target: PreviewTarget): String

    /** False for a device too old to answer range reads (spec §2.4): nothing is asked of it. */
    fun canRead(side: FileSide): Boolean = true

    /** False for a device too old to answer `file_hash_request`. */
    fun canHash(side: FileSide): Boolean = true

    /** Decodes a whole image, scaled to fit the screen; null when it isn't one this phone can draw. */
    suspend fun decodeImage(bytes: ByteArray): I?
}

/**
 * The preview pane's state (file browser redesign, 2026-10-08), the phone's twin of the PC's
 * `FilePreviewViewModel`: it follows the selection after a short pause, shows images whole and text with
 * colour, follows a log live, and computes, compares and cross-checks SHA-256 fingerprints. Android-free apart
 * from the image type [I], so every state is unit-tested.
 */
class FilePreviewModel<I>(
    private val scope: CoroutineScope,
    private val sources: PreviewSources<I>,
    private val debounceMs: Long = 150,
    private val pollMs: Long = FileTransferLimits.PREVIEW_TAIL_POLL_MS,
    private val maxTailLines: Int = MAX_TAIL_LINES,
) {
    private val _ui = MutableStateFlow(PreviewUi<I>())
    val ui: StateFlow<PreviewUi<I>> = _ui.asStateFlow()

    private var loadJob: Job? = null
    private var tailJob: Job? = null
    private var hashJob: Job? = null
    private var verifyJob: Job? = null
    private var language = SyntaxLanguage.Plain

    /** Shows [target] after a short pause, so flicking through a list doesn't fetch every file on the way. */
    fun show(target: PreviewTarget?) {
        if (target == _ui.value.target && _ui.value.state != PreviewState.Failed) return
        cancelAll()
        if (target == null) {
            _ui.value = PreviewUi()
            return
        }
        val other = if (target.side == FileSide.Phone) FileSide.Pc else FileSide.Phone
        val canHash = sources.canHash(target.side)
        _ui.value = PreviewUi(
            target = target,
            state = PreviewState.Loading,
            canHash = canHash,
            canVerifyAcross = canHash && sources.canHash(other),
        )
        loadJob = scope.launch {
            delay(debounceMs)
            load(target)
        }
    }

    /** Loads [target] now. Internal so tests skip the pause. */
    internal suspend fun load(target: PreviewTarget) {
        language = SyntaxTokenizer.languageFor(target.name)
        val kind = PreviewClassifier.classify(target.name, target.isDirectory)
        try {
            when {
                kind == PreviewKind.None || kind == PreviewKind.ImageThumbnailOnly -> set(target) { it.copy(state = PreviewState.Details) }
                !sources.canRead(target.side) -> set(target) { it.copy(state = PreviewState.NeedsUpdate) }
                else -> loadReadable(target, kind)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: PreviewTooLargeException) {
            set(target) { it.copy(state = PreviewState.TooLarge, fileSize = e.fileSize) }
        } catch (_: Exception) {
            set(target) { it.copy(state = PreviewState.Failed) }
        }
    }

    private suspend fun loadReadable(target: PreviewTarget, kind: PreviewKind) {
        when (kind) {
            PreviewKind.Image -> loadImage(target)
            PreviewKind.Text, PreviewKind.Sniff -> loadText(target, sniffed = kind == PreviewKind.Sniff)
            PreviewKind.None, PreviewKind.ImageThumbnailOnly -> Unit
        }
    }

    private suspend fun loadImage(target: PreviewTarget) {
        if (target.sizeBytes > FileTransferLimits.PREVIEW_IMAGE_MAX_BYTES) {
            throw PreviewTooLargeException(target.sizeBytes, FileTransferLimits.PREVIEW_IMAGE_MAX_BYTES)
        }
        val bytes = ImagePreviewLoader.load(sources.reader(target), { p -> set(target) { it.copy(progress = p) } })
        val image = sources.decodeImage(bytes)
        set(target) {
            if (image == null) it.copy(state = PreviewState.Details, fileSize = bytes.size.toLong())
            else it.copy(state = PreviewState.Image, image = image, fileSize = bytes.size.toLong(), progress = 1f)
        }
    }

    private suspend fun loadText(target: PreviewTarget, sniffed: Boolean) {
        val head = TextPreviewLoader.loadHead(sources.reader(target))
        if (head.isBinary) {
            // A file whose name promised text but holds bytes says so; an unknown one just shows its details.
            set(target) { it.copy(state = if (sniffed) PreviewState.Details else PreviewState.Binary, fileSize = head.fileSize) }
            return
        }
        var raw = head.text.split('\n')
        if (head.text.endsWith('\n')) raw = raw.dropLast(1)
        val lines = raw.mapIndexed { i, line -> line.trimEnd('\r').let { PreviewLine(i + 1, it, SyntaxTokenizer.tokenize(it, language)) } }
        set(target) { it.copy(state = PreviewState.Text, lines = lines, truncated = head.truncated, fileSize = head.fileSize) }
    }

    /** Follows the file live (the last 256 KiB, then whatever is added every two seconds), or stops following. */
    fun setLive(on: Boolean) {
        val current = _ui.value
        if (on == current.live || (on && !current.canTail)) return
        tailJob?.cancel()
        tailJob = null
        val target = current.target ?: return
        _ui.update { it.copy(live = on) }
        if (!on) return
        tailJob = scope.launch {
            val tail = TextTail(sources.reader(target))
            try {
                apply(target, tail.start())
                while (true) {
                    delay(pollMs)
                    apply(target, tail.poll())
                }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                // The file went away or the connection dropped: stop following and keep what is shown.
                set(target) { it.copy(live = false) }
            }
        }
    }

    internal fun apply(target: PreviewTarget, update: TailUpdate) = set(target) { ui ->
        val added = update.lines.map { PreviewLine(null, it, SyntaxTokenizer.tokenize(it, language)) }
        var lines = if (update.reset) added else ui.lines + added
        if (lines.size > maxTailLines) lines = lines.subList(lines.size - maxTailLines, lines.size)
        ui.copy(lines = lines, pendingLine = update.pendingLine, truncated = false, fileSize = update.fileSize)
    }

    /** Computes this file's SHA-256 (on the phone for phone files, on the PC for PC files). */
    fun computeHash() {
        val target = _ui.value.target ?: return
        if (target.isDirectory || _ui.value.hashing || !_ui.value.canHash) return
        hashJob?.cancel()
        set(target) { it.copy(hashing = true, hashFailed = false) }
        hashJob = scope.launch {
            val hex = try {
                HashFormat.toHex(sources.hashBase64(target))
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                null
            }
            set(target) { it.copy(hashing = false, hashHex = hex, hashFailed = hex == null) }
        }
    }

    /** Compares the computed fingerprint with one the person pasted, in hex or Base64. */
    fun compare(pasted: String) {
        val hex = _ui.value.hashHex
        val outcome = when {
            pasted.isBlank() -> HashCompareOutcome.None
            HashFormat.normalize(pasted) == null -> HashCompareOutcome.NotAHash
            hex == null -> HashCompareOutcome.None
            HashFormat.matches(hex, pasted) -> HashCompareOutcome.Match
            else -> HashCompareOutcome.Mismatch
        }
        _ui.update { it.copy(compare = outcome) }
    }

    /** Checks this file against [other] on the other device: identical or different, by SHA-256. */
    fun verifyAgainst(other: PreviewTarget) {
        val target = _ui.value.target ?: return
        if (target.isDirectory || other.isDirectory || _ui.value.verifying || !_ui.value.canVerifyAcross) return
        set(target) { it.copy(verifying = true, counterpart = CounterpartOutcome.None) }
        verifyJob?.cancel()
        // Tracked, so moving to another file stops a 30-minute hash on both devices instead of letting it run on.
        verifyJob = scope.launch {
            val outcome = try {
                val mine = _ui.value.hashHex ?: HashFormat.toHex(sources.hashBase64(target))
                val theirs = HashFormat.toHex(sources.hashBase64(other))
                set(target) { it.copy(hashHex = mine) }
                if (mine != null && HashFormat.matches(mine, theirs)) CounterpartOutcome.Identical else CounterpartOutcome.Different
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                CounterpartOutcome.Failed
            }
            set(target) { it.copy(verifying = false, counterpart = outcome) }
        }
    }

    /** Stops everything (the screen was left). */
    fun close() {
        cancelAll()
        _ui.value = PreviewUi()
    }

    private fun cancelAll() {
        loadJob?.cancel()
        tailJob?.cancel()
        hashJob?.cancel()
        verifyJob?.cancel()
        loadJob = null
        tailJob = null
        hashJob = null
        verifyJob = null
    }

    /** Applies [change] only while [target] is still the one shown, so a slow answer never lands on the next file. */
    private inline fun set(target: PreviewTarget, change: (PreviewUi<I>) -> PreviewUi<I>) {
        _ui.update { if (it.target == target) change(it) else it }
    }

    companion object {
        /** A live tail keeps at most this many lines; older ones scroll off the top. */
        const val MAX_TAIL_LINES = 5000
    }
}
