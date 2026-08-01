using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;

namespace Remex.Agent.Services.RemoteDesktop;

public sealed class FFmpegH264Encoder : IH264Encoder
{
    private readonly ILogger _logger;
    private Process? _ffmpegProcess;

    // Bounded, drop-newest feed from the capture thread to the dedicated stdin writer. A tiny
    // capacity (a few frames) is intentional: if the encoder can't keep up, the freshest frames
    // matter, not a backlog, so EncodeFrame drops rather than blocking the capture thread. (RemEx-ii3)
    private const int InputChannelCapacity = 3;
    // ReadOnlyMemory rather than byte[] so a caller can hand over a slice of a larger buffer without
    // trimming it first (RemEx-hgox). OWNERSHIP IS UNCHANGED by that: a ReadOnlyMemory over a GC array
    // behaves exactly as the array did, and nothing here pools or recycles. Do not read this as a step
    // toward pooling - see RemEx-lcp8, which has failed three times precisely because a recycled
    // buffer can be overwritten while stdin is still reading it.
    private Channel<ReadOnlyMemory<byte>>? _inputChannel;
    private Task? _writerTask;

    // Bounded output of encoded Annex-B access units. Replaces an unbounded queue so a stalled/slow
    // sender can't grow memory without limit. FullMode is DropWrite (not DropOldest, RemEx-3u6o):
    // silently evicting an already-queued access unit mid-GOP corrupts the reference chain for every
    // frame after it — the decoder macroblocks until the next IDR with no way to detect the gap. With
    // DropWrite the eviction is detectable (TryWrite returns false), so QueueFrame can drain the stale
    // backlog and enter drop-until-IDR mode instead of forwarding a broken chain. (RemEx-fs5)
    private const int OutputChannelCapacity = 8;
    private Channel<byte[]>? _encodedFrames;

    // Drop-until-IDR state (RemEx-3u6o): once the encoded-output channel overflows, forwarding more
    // P-frames referencing the frames that got evicted would just macroblock — so QueueFrame drops
    // everything until the next independently-decodable access unit (IDR) instead.
    private bool _droppingUntilIdr;
    private long _droppedAccessUnits;
    private long _lastDropLogMs;

    // Accepted-input-frame counter for AdaptiveScaleController (Phase 5, RemEx-eo0f): incremented
    // whenever EncodeFrame's TryWrite onto the (drop-write) input channel actually succeeds, so a
    // sampled delta over an evaluation window is a direct measure of achieved encode FPS.
    private long _acceptedInputFrames;

    public long AcceptedInputFrameCount => Interlocked.Read(ref _acceptedInputFrames);
    public long DroppedAccessUnitCount => Interlocked.Read(ref _droppedAccessUnits);

    // Hard cap on the Annex-B reassembly accumulator. A healthy stream cuts an access unit at every
    // AUD (well under this). If we ever buffer this much WITHOUT seeing an AUD start code, the input
    // is malformed/desynced — reset the accumulator instead of growing toward OOM. (RemEx-fs5)
    private const int MaxAccumulatorBytes = 8 * 1024 * 1024;

    private Thread? _readerThread;

    // Owns cancellation for every async pump this encoder spawns (stdin writer, stderr reader). Lets
    // DisposeProcess tear them down promptly and stops them from touching a disposed logger. (RemEx-aa0)
    private CancellationTokenSource? _processCts;

    // Pending on-demand keyframe request (0/1). Set by RequestKeyframe (any thread), read-and-cleared
    // by ConsumeKeyframeRequest on the capture loop, which then forces an encoder reinit → real IDR.
    private int _keyframeRequested;

    // Cache of one-shot capability-probe outcomes keyed by codec+geometry. Capability is a
    // property of the machine (GPU/driver/ffmpeg build), not of one encoder instance, and the
    // handler reinitializes the encoder on every on-demand keyframe — without the cache each
    // reinit would re-pay an ffmpeg probe spawn. (RemEx: probe added for the lazy encoder-open
    // blind spot; see ProbeCodec.)
    //
    // Positive verdicts are stable and cached for the process lifetime. Negative verdicts are
    // NOT: a probe can fail transiently (5s timeout while the GPU/display is reconfiguring
    // during a monitor switch, or portal/driver churn on reconnect), and a permanently cached
    // failure used to pin that geometry to MJPEG until the host restarted. Failed verdicts are
    // therefore retried after a cooldown. (RemEx-lq6h)
    private static readonly ConcurrentDictionary<string, (bool Ok, long AtMs)> ProbeCache = new();
    private const long FailedProbeRetryMs = 30_000;

    private bool _isDisposed;
    private string? _ffmpegPath;
    private int _width;
    private int _height;
    private int _inputWidth;
    private int _inputHeight;
    private bool _initialized;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Bytes the caller must write per frame. Follows the INPUT size, not the encoded size — ffmpeg's
    /// <c>-s</c> describes what arrives on the pipe, and a mismatch here desyncs rawvideo and makes
    /// the encoder emit zero frames. 0 until <see cref="Initialize"/> succeeds.
    /// THE PROBE MUST USE THE SAME SIZE; see RunEncoderProbe.
    /// </summary>
    public int ExpectedInputByteCount => _initialized ? _inputWidth * _inputHeight * 4 : 0;

    /// <summary>
    /// The FFmpeg codec string that was successfully started (e.g. "h264_nvenc", "libx264").
    /// Null if not yet initialized.
    /// </summary>
    public string? ActiveCodecName { get; private set; }

    public FFmpegH264Encoder(ILogger logger)
    {
        _logger = Guard.NotNull(logger);
        DetectFFmpeg();
    }

    private void DetectFFmpeg()
    {
        try
        {
            // 1. Check system path
            _ffmpegPath = FindExecutable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg");

            // 2. Check standard Windows installations if not in path
            if (_ffmpegPath == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var commonPaths = new[]
                {
                    @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                    @"C:\ffmpeg\bin\ffmpeg.exe",
                    @"D:\ffmpeg\bin\ffmpeg.exe",
                    @"E:\utilities\ffmpeg\bin\ffmpeg.exe"
                };

                foreach (var path in commonPaths)
                {
                    if (File.Exists(path))
                    {
                        _ffmpegPath = path;
                        break;
                    }
                }
            }

            IsAvailable = _ffmpegPath != null;
            if (IsAvailable)
            {
                _logger.LogInformation("FFmpeg H.264 encoder detected at: {Path}", _ffmpegPath);
            }
            else
            {
                _logger.LogWarning("FFmpeg not found. H.264 video streaming will be unavailable (falling back to MJPEG).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detect FFmpeg.");
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Starts the encoder. <paramref name="inputWidth"/>/<paramref name="inputHeight"/> are the size
    /// of the raw BGRA frames the caller will write; <paramref name="width"/>/<paramref name="height"/>
    /// are the size to encode. When they differ ffmpeg does the downscale — see
    /// <see cref="BuildEncoderArgs"/> for why that beats scaling on the capture thread.
    /// </summary>
    public bool Initialize(int inputWidth, int inputHeight, int width, int height, int fps, int qp)
    {
        if (!IsAvailable || _ffmpegPath == null) return false;
        if (_initialized) DisposeProcess();

        // Constant-QP rate control. Lower QP = higher quality + bitrate. Clamp to a sane H.264 range.
        qp = Math.Clamp(qp, 16, 45);

        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _width = width;
        _height = height;

        // Hardware-accelerated codecs to try in order of preference
        string[] codecsToTry;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Try NVENC with NATIVE BGRA input FIRST (BGRA->YUV runs in fixed-function NVENC hardware —
            // no CPU swscale, no CUDA filter); falls back to the CPU-convert NVENC path if it can't start.
            codecsToTry = new[] { "h264_nvenc_bgra", "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
        }
        else // Linux
        {
            // Same NVENC-native-BGRA fast path first as on Windows: it skips the per-frame CPU
            // swscale (BGRA->YUV runs in fixed-function NVENC hardware) that otherwise caps FPS
            // well below the target (Linux half of RemEx-whfp). h264_vaapi stays for AMD/Intel
            // boxes — on NVIDIA it can never open (the NVIDIA VAAPI driver is decode-only), and
            // the capability probe below skips it there instead of silently killing the stream.
            codecsToTry = new[] { "h264_nvenc_bgra", "h264_nvenc", "h264_vaapi", "libx264" };
        }

        foreach (var codec in codecsToTry)
        {
            // Probe first: a rawvideo-pipe ffmpeg only opens its encoder when the first frame
            // arrives, so TryStartFFmpeg's early-exit watch cannot see encoder-open failures
            // (h264_vaapi on NVIDIA was reported "initialized", died on the first real frame,
            // and the session silently degraded to MJPEG — RemEx-h038). The one-shot probe
            // forces that lazy open up front so fallback to the next codec is deterministic.
            if (!ProbeCodec(codec, inputWidth, inputHeight, width, height, fps, qp))
                continue;

            if (TryStartFFmpeg(codec, inputWidth, inputHeight, width, height, fps, qp))
            {
                long maxRate = ComputeMaxRateBps(width, height, fps, qp);
                _logger.LogInformation(
                    "FFmpeg H.264 encoder successfully initialized using codec: {Codec} (qp/cq={Qp}, maxrate={MaxRate}bps, bufsize={BufSize}bps, gop={Gop}).",
                    codec, qp, maxRate, ComputeBufSizeBps(maxRate), ComputeGop(fps));
                ActiveCodecName = codec;
                _initialized = true;
                return true;
            }
        }

        _logger.LogError("Failed to start FFmpeg with any H.264 encoder codec.");
        return false;
    }

    /// <summary>
    /// Bits-per-pixel-per-frame budget derived from QP: QP16 (sharpest) ≈ 0.16, scaling linearly
    /// down to a 0.02 floor at QP38+. Multiplied by resolution × fps to get a bitrate ceiling that
    /// tracks how much motion/detail the stream can actually carry, instead of the unbounded
    /// constant-QP bitrate that used to spike with FPS×motion and overflow the output channel.
    /// </summary>
    internal static long ComputeMaxRateBps(int width, int height, int fps, int qp)
    {
        double bpp = 0.16 - (Math.Clamp(qp, 16, 45) - 16) * (0.11 / 22.0);
        bpp = Math.Max(bpp, 0.02);
        return Math.Clamp((long)((double)width * height * fps * bpp), 2_000_000L, 60_000_000L);
    }

    /// <summary>VBV buffer size: half the max-rate window (~0.5s), the standard low-latency VBV sizing.</summary>
    internal static long ComputeBufSizeBps(long maxRate) => maxRate / 2;

    /// <summary>GOP length scales with fps so a keyframe-less encoder never leaves a desynced
    /// decoder waiting more than ~1s to self-heal, while still amortizing IDR cost at high fps.</summary>
    internal static int ComputeGop(int fps) => Math.Clamp(fps, 30, 120);

    /// <summary>
    /// Builds the full ffmpeg argument string for <paramref name="codec"/>. Shared by the real
    /// streaming process and the one-shot capability probe so the probe exercises EXACTLY the
    /// arguments the real run will use — only the output side differs (single frame to a
    /// discarded null muxer vs. a flushed Annex-B stream on stdout). <paramref name="inputWidth"/>/<paramref name="inputHeight"/>
    /// describe the raw BGRA frames arriving on the pipe; <paramref name="width"/>/
    /// <paramref name="height"/> are the ENCODED dimensions.
    /// </summary>
    /// <remarks>
    /// When they differ, a <c>scale</c> filter does the downscale inside ffmpeg. That is the point of
    /// separating them: the capture backend used to shrink the frame itself with a GDI+ bilinear
    /// <c>DrawImage</c>, measured at <b>20.7 ms per frame</b> for 2560x1440 → 0.6 on the reference box
    /// — single-threaded, on the capture thread, capping the stream near 48 FPS before the encoder saw
    /// anything. Handing full-resolution frames to swscale instead costs 1.7 ms of row-copy on the
    /// capture thread and moves the resample into ffmpeg's SIMD scaler on its own threads (RemEx-evzv).
    /// <para>
    /// The trade is pipe bandwidth: full-res 2560x1440 BGRA is 14 MB per frame against 5.3 MB at 0.6.
    /// It is worth it because the capture thread is what gates frame rate, and 20.7 ms of it cannot be
    /// spent 120 times a second.
    /// </para>
    /// <para>
    /// <c>bilinear</c>, NOT <c>fast_bilinear</c>. Measured on the reference box at 2560x1440 → 1536x864
    /// against a lanczos reference: fast_bilinear 28.93 dB / 3.55 ms, bilinear 42.87 dB / 3.87 ms. The
    /// extra 0.32 ms buys ~14 dB and is free against an 8.3 ms budget at 120 FPS on ffmpeg's own
    /// thread. The initial choice of fast_bilinear also had the rate-distortion argument backwards:
    /// scaler aliasing costs MORE bits at a given QP, so it hurts H.264 efficiency as well as sharpness.
    /// </para>
    /// </remarks>
    internal static string BuildEncoderArgs(
        string codec, int inputWidth, int inputHeight, int width, int height, int fps, int qp, bool forProbe)
    {
        var argsBuilder = new StringBuilder();

        // Input: Raw BGRA frames from stdin, at the size the CAPTURE produces.
        argsBuilder.Append($"-f rawvideo -pix_fmt bgra -s {inputWidth}x{inputHeight} -r {fps} -i - -an ");

        // Downscale inside ffmpeg when capture and encode sizes differ. Prepended to each codec's own
        // filter chain below rather than emitted as a second -vf, which would silently override it.
        string scaleFilter = inputWidth != width || inputHeight != height
            ? $"scale={width}:{height}:flags=bilinear"
            : string.Empty;

        // VBV-capped rate control budget, shared across every codec below. Replaces the previous
        // unbounded constant-QP scheme (no -maxrate/-bufsize anywhere), whose bitrate scaled freely
        // with FPS×motion and overran the bounded encoded-output channel — the overrun evicted
        // encoded access units mid-GOP, which the decoder saw as macroblocking until the next IDR.
        long maxRate = ComputeMaxRateBps(width, height, fps, qp);
        long bufSize = ComputeBufSizeBps(maxRate);
        int gop = ComputeGop(fps);

        // Codec & Quality Optimization. qp/cq anchors quality; maxrate/bufsize cap the peak so a
        // busy frame can't blow the output channel, and gop scales with fps so on-demand recovery
        // (natural GOP keyframe) never waits more than ~1s.
        switch (codec)
        {
            case "h264_nvenc_bgra":
                // Fast GPU path: NVENC ingests the raw BGRA frames DIRECTLY (the input above is already
                // `-pix_fmt bgra`) and does BGRA->YUV in fixed-function hardware. No `-pix_fmt yuv420p`,
                // so no per-frame CPU swscale (~20ms at 1440p on the plain path) and the GPU isn't idle;
                // no CUDA filter either. Tried FIRST; falls through to the CPU-convert h264_nvenc path
                // below if NVENC can't start. ActiveCodecName reports which path won.
                //
                // The previous approach used `hwupload_cuda,scale_cuda=format=nv12`, but scale_cuda in
                // every prebuilt Windows ffmpeg (Gyan + BtbN, both --enable-cuda-llvm) lacks the
                // RGB->NV12 kernel and dies at RUNTIME with CUDA_ERROR_NOT_FOUND ("named symbol not
                // found" / "Unsupported conversion: rgb0 -> semiplanar8"), killing FFmpeg mid-stream —
                // 0fps black screen. NVENC native BGRA input avoids the filter entirely. (RemEx-dptu)
                //
                // -rc vbr -cq {qp}: quality-anchored VBR (constqp had no bitrate ceiling at all).
                //
                // In-band SPS/PPS on every IDR: nvenc has no explicit AVOption for this — ffmpeg sets
                // repeatSPSPPS whenever AV_CODEC_FLAG_GLOBAL_HEADER is unset, which is always true for
                // raw `-f h264 -` output, so every natural GOP keyframe is already self-contained and
                // a decoder joining mid-stream configures from the next one. (RemEx-vj7b)
                //
                // The scale filter, when present, stays in BGRA: swscale resizes and NVENC still gets
                // BGRA to convert in fixed-function hardware, so this path keeps its whole point. It
                // is deliberately NOT scale_cuda for the reason above.
                if (scaleFilter.Length > 0) argsBuilder.Append($"-vf {scaleFilter} ");
                argsBuilder.Append($"-c:v h264_nvenc -preset p1 -tune ll -rc vbr -cq {qp} -b:v 0 -maxrate {maxRate} -bufsize {bufSize} -g {gop} -forced-idr 1 -aud 1");
                break;
            case "h264_nvenc":
                // NVIDIA NVENC. `-tune ll` = low latency (valid values are hq/ll/ull/lossless;
                // "lowlatency" is NOT valid and makes nvenc fail to start). `-aud 1` emits Access
                // Unit Delimiters, which the stdout reader relies on to split encoded frames.
                // `-pix_fmt yuv420p` keeps output to 8-bit 4:2:0 that every H.264 decoder accepts.
                // `-forced-idr 1` makes the forced keyframes emitted by `-force_key_frames` true
                // IDR frames (with fresh SPS/PPS) instead of plain non-IDR I-frames, so an
                // on-demand keyframe is independently decodable by a desynced client. (RemEx-bqc)
                if (scaleFilter.Length > 0) argsBuilder.Append($"-vf {scaleFilter} ");
                argsBuilder.Append($"-c:v h264_nvenc -preset p1 -tune ll -rc vbr -cq {qp} -b:v 0 -maxrate {maxRate} -bufsize {bufSize} -g {gop} -forced-idr 1 -aud 1 -pix_fmt yuv420p");
                break;
            case "h264_vaapi":
                // VA-API on Linux (Intel/AMD). Rate-control left as CQP — VBR/maxrate behavior varies
                // per driver and needs separate Linux validation (tested on Windows only for Phase 3).
                argsBuilder.Append($"-vaapi_device /dev/dri/renderD128 -vf ");
                if (scaleFilter.Length > 0) argsBuilder.Append($"{scaleFilter},");
                argsBuilder.Append($"format=nv12,hwupload -c:v h264_vaapi -qp {qp} -g {gop} -aud 1");
                break;
            case "h264_qsv":
                // Intel Quick Sync. QVBR: -global_quality anchors quality, -maxrate/-bufsize cap peaks.
                // -repeat_pps 1: QSV does not re-emit the PPS on IDRs by default, so a decoder that
                // missed the stream-start access unit could never configure from natural GOP
                // keyframes — black until a full restart. (RemEx-vj7b)
                if (scaleFilter.Length > 0) argsBuilder.Append($"-vf {scaleFilter} ");
                argsBuilder.Append($"-c:v h264_qsv -preset veryfast -look_ahead 0 -global_quality {qp} -maxrate {maxRate} -bufsize {bufSize} -g {gop} -forced-idr 1 -repeat_pps 1 -aud 1");
                break;
            case "h264_amf":
                // AMD AMF. vbr_peak: -b:v is the target (75% of the ceiling), -maxrate the hard peak.
                // -header_spacing {gop}: AMF's default effectively emits SPS/PPS only once, so a
                // decoder that missed the stream-start access unit could never configure from natural
                // GOP keyframes — black until a full restart. Spacing == GOP length aligns header
                // insertion with the periodic IDRs (AMD's recommended streaming setup). NOTE: this is
                // the H.264 AMF option — `header_insertion_mode` exists only on hevc_amf. (RemEx-vj7b)
                if (scaleFilter.Length > 0) argsBuilder.Append($"-vf {scaleFilter} ");
                argsBuilder.Append($"-c:v h264_amf -quality speed -rc vbr_peak -b:v {maxRate * 3 / 4} -maxrate {maxRate} -bufsize {bufSize} -g {gop} -forced-idr 1 -header_spacing {gop} -aud 1");
                break;
            default:
                // libx264 software fallback with zero latency. libx264 has no generic
                // `-aud` AVOption; AUD emission is requested via x264 params instead. x264 always
                // makes a forced keyframe a true IDR, so no extra flag is needed for it.
                // -crf stays quality-anchored; -maxrate/-bufsize turn it into VBV-constrained CRF.
                // repeat-headers=1: already x264's default without GLOBAL_HEADER, made explicit so
                // self-contained GOP keyframes are a tested contract, not an ffmpeg default that a
                // future muxer/global-header change could silently revoke. (RemEx-vj7b)
                if (scaleFilter.Length > 0) argsBuilder.Append($"-vf {scaleFilter} ");
                argsBuilder.Append($"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf {qp} -maxrate {maxRate} -bufsize {bufSize} -g {gop} -x264-params aud=1:repeat-headers=1");
                break;
        }

        // On-demand keyframes. `-force_key_frames` with the `expr:` form lets us request an IDR at
        // an arbitrary point: writing to the sentinel file toggles `gte(...)` — see RequestKeyframe.
        // The interval-expression below ('expr:gte(t,n_forced*...)') is a no-op safety net; the real
        // trigger is the host bumping the GOP via reinit (RequestKeyframe), which the higher layer
        // already supports. We keep the codec emitting forced IDRs (above) so that path is real.
        //
        // Output raw Annex B H.264 stream to stdout.
        // -flush_packets 1 forces ffmpeg to flush stdout after every packet instead of
        // block-buffering it, which is required for low-latency real-time piping (otherwise
        // encoded frames sit in ffmpeg's buffer and the stream stalls).
        //
        // Probe mode instead encodes a single frame into a discarded null muxer: the encoder
        // opens exactly as in the real run, and the process exits with 0 (codec works at
        // this geometry) or nonzero (it does not).
        argsBuilder.Append(forProbe ? " -frames:v 1 -f null -" : " -flush_packets 1 -f h264 -");

        return argsBuilder.ToString();
    }

    /// <summary>
    /// One-shot capability probe with a cached verdict: encodes a single black frame using the
    /// exact codec arguments and input geometry of the real run, to a discarded output. A
    /// rawvideo-pipe ffmpeg only opens its encoder when the first frame arrives, so watching the
    /// real process for early exit can never catch encoder-open failures — the probe forces the
    /// lazy open to happen up front. Exit code 0 means the codec genuinely encodes at this
    /// geometry. (RemEx-h038)
    /// </summary>
    private bool ProbeCodec(string codec, int inputWidth, int inputHeight, int width, int height, int fps, int qp)
    {
        // qp is excluded from the key on purpose: it is clamped to a universally valid H.264
        // range in Initialize and never decides whether an encoder can open.
        // Input dims are part of the key: they change the filter chain (a scale filter appears or
        // disappears), so a probe for the same OUTPUT size is not transferable between them.
        var cacheKey = $"{codec}:{inputWidth}x{inputHeight}->{width}x{height}@{fps}";
        if (ProbeCache.TryGetValue(cacheKey, out var cached) &&
            (cached.Ok || Environment.TickCount64 - cached.AtMs < FailedProbeRetryMs))
        {
            return cached.Ok;
        }

        bool ok;
        try
        {
            ok = RunEncoderProbe(codec, inputWidth, inputHeight, width, height, fps, qp);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FFmpeg capability probe for {Codec} threw; treating codec as unavailable.", codec);
            ok = false;
        }

        ProbeCache[cacheKey] = (ok, Environment.TickCount64);
        return ok;
    }

    private bool RunEncoderProbe(string codec, int inputWidth, int inputHeight, int width, int height, int fps, int qp)
    {
        var psi = new ProcessStartInfo(_ffmpegPath!, BuildEncoderArgs(codec, inputWidth, inputHeight, width, height, fps, qp, forProbe: true))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        // Drain both output pipes concurrently so a chatty ffmpeg can never fill one and deadlock.
        var stderrTask = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();

        try
        {
            // Feed exactly one black frame, then EOF. Written in small chunks so the probe never
            // allocates a whole frame (≈14 MB at 2560x1440) on the large-object heap.
            //
            // SIZED FROM THE INPUT DIMENSIONS, which is the whole pipe contract: -s describes what
            // arrives, so writing the ENCODED size here makes ffmpeg reject the frame with
            // "packet size N < expected frame_size M" and fail the probe for EVERY codec — which
            // presents as H.264 silently falling back to MJPEG forever, not as an error. That is
            // exactly what this change did in review before the length was corrected (RemEx-evzv).
            var stdin = process.StandardInput.BaseStream;
            var zeros = new byte[64 * 1024];
            long remaining = (long)inputWidth * inputHeight * 4;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(zeros.Length, remaining);
                stdin.Write(zeros, 0, chunk);
                remaining -= chunk;
            }
            stdin.Close();
        }
        catch (IOException)
        {
            // Broken pipe: ffmpeg exited before consuming the frame (unknown encoder, no
            // device, ...). The exit code below reports the failure.
        }

        // A single frame encodes in well under a second on every codec here; 5s covers slow
        // hardware/driver initialization without letting a wedged probe stall the stream start.
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _logger.LogWarning("FFmpeg capability probe for codec {Codec} timed out; treating codec as unavailable.", codec);
            return false;
        }

        if (process.ExitCode == 0)
            return true;

        // The child has exited (WaitForExit above, and the timeout branch returns rather than
        // falling through), so the pipe's write end is closed and EOF is guaranteed to arrive - this
        // cannot hang. It is NOT guaranteed to be complete already, though: WaitForExit(int) does not
        // wait for redirected-stream EOF the way the parameterless overload does, so this can block
        // briefly while the reader drains. Harmless here - the probe thread has no
        // SynchronizationContext - but it is a blocking wait, not a free read. (RemEx-r9tv)
        var stderr = stderrTask.GetAwaiter().GetResult().Trim();
        var stderrTail = stderr.Length > 400 ? stderr[^400..] : stderr;
        _logger.LogWarning(
            "FFmpeg codec {Codec} failed capability probe at {Width}x{Height}@{Fps} (exit {Code}); " +
            "trying next codec. stderr tail: {Stderr}",
            codec, width, height, fps, process.ExitCode, stderrTail);
        return false;
    }

    private bool TryStartFFmpeg(string codec, int inputWidth, int inputHeight, int width, int height, int fps, int qp)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath!, BuildEncoderArgs(codec, inputWidth, inputHeight, width, height, fps, qp, forProbe: false))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process == null) return false;

            // Safety net: watch for early process exit ("Unknown encoder", "Cannot load
            // nvcuda", bad option). Encoder-open failures that only surface once the first
            // frame arrives (e.g. h264_vaapi on NVIDIA) can NOT be seen here — a rawvideo-pipe
            // ffmpeg idles waiting for stdin until then — which is why Initialize runs
            // ProbeCodec before ever calling this method (RemEx-h038). A healthy rawvideo-pipe
            // encoder never exits on its own, so an exit within this window unambiguously means
            // failure. The cost is a one-time startup wait for the codec that actually succeeds.
            if (process.WaitForExit(900))
            {
                var error = process.StandardError.ReadToEnd();
                _logger.LogWarning("FFmpeg codec {Codec} failed to start (exit {Code}): {Error}",
                    codec, process.ExitCode, error.Trim());
                process.Dispose();
                return false;
            }

            _ffmpegProcess = process;

            // Fresh per-process cancellation source + bounded pipes. The output channel is drop-oldest
            // so it can never block the reader; the input channel is drop-write so EncodeFrame never
            // blocks the capture thread (drops are handled explicitly in EncodeFrame). (RemEx-ii3/fs5)
            _processCts = new CancellationTokenSource();
            var ct = _processCts.Token;

            _encodedFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutputChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = true,
            });
            _droppingUntilIdr = false;

            _inputChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(InputChannelCapacity)
            {
                // We still TryWrite (non-blocking) in EncodeFrame, but DropWrite makes the contract
                // explicit: a full channel discards the newest frame rather than ever blocking.
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });

            var stdin = process.StandardInput.BaseStream;
            _writerTask = Task.Run(() => StdinWriterLoop(stdin, _inputChannel.Reader, ct));

            // Start reader thread to parse Annex B stream from stdout
            _readerThread = new Thread(() => ReaderLoop(process.StandardOutput.BaseStream))
            {
                Name = "RemexFFmpegH264Reader",
                IsBackground = true
            };
            _readerThread.Start();

            // Track stderr in background to log errors if any. Bound it to the encoder's CTS so it stops
            // promptly on disconnect/dispose and never logs through a disposed logger. (RemEx-aa0)
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = process.StandardError;
                    while (!ct.IsCancellationRequested && !process.HasExited)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null) break;
                        if (line.Contains("Error", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("FFmpeg stderr: {Line}", line);
                        }
                    }
                }
                catch (OperationCanceledException) { /* encoder torn down */ }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "FFmpeg stderr reader exited.");
                }
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to start FFmpeg with codec {Codec}", codec);
            return false;
        }
    }

    /// <summary>
    /// Dedicated stdin writer. Drains the bounded input channel and writes each raw BGRA frame to
    /// ffmpeg's stdin with <see cref="Stream.WriteAsync(ReadOnlyMemory{byte},CancellationToken)"/> so
    /// the capture thread is never blocked on a slow/busy encoder. Stops promptly when the token is
    /// cancelled (disconnect/dispose). (RemEx-ii3)
    /// </summary>
    private async Task StdinWriterLoop(Stream stdin, ChannelReader<ReadOnlyMemory<byte>> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                if (_ffmpegProcess is { HasExited: true }) break;
                await stdin.WriteAsync(frame, ct);
                await stdin.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* encoder torn down */ }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Error writing frame to FFmpeg stdin.");
        }
        finally
        {
            try { stdin.Dispose(); } catch { /* best effort: closing stdin signals EOF to ffmpeg */ }
        }
    }

    private void ReaderLoop(Stream stdout)
    {
        // Splits the Annex B stream into complete access units on AUD boundaries (00 00 00 01 09).
        // Each access unit (one decodable frame, prefixed by its AUD, and SPS/PPS at IDRs) is queued
        // intact. We rely on -aud 1 / x264 aud=1 so every frame begins with an AUD NAL.
        var read = new byte[1 << 16];
        var acc = new byte[1 << 20];
        int accLen = 0;

        try
        {
            while (!_isDisposed && _ffmpegProcess is { HasExited: false })
            {
                int bytesRead = stdout.Read(read, 0, read.Length);
                if (bytesRead <= 0) break;

                // Malformed-input guard: if the accumulator has grown past the hard cap WITHOUT us
                // finding an AUD to cut on (so accLen never shrank), the stream is desynced/garbage.
                // Resetting here keeps a bad input from growing the buffer toward OOM. (RemEx-fs5)
                if (accLen + bytesRead > MaxAccumulatorBytes)
                {
                    _logger.LogWarning(
                        "H.264 Annex-B accumulator exceeded {Cap} bytes without an AUD boundary; " +
                        "resetting (malformed/desynced encoder output).", MaxAccumulatorBytes);
                    accLen = 0;
                    continue;
                }

                // Append to accumulator, growing if needed (bounded by MaxAccumulatorBytes above).
                if (accLen + bytesRead > acc.Length)
                {
                    int newSize = Math.Min(MaxAccumulatorBytes, Math.Max(acc.Length * 2, accLen + bytesRead));
                    Array.Resize(ref acc, newSize);
                }
                Buffer.BlockCopy(read, 0, acc, accLen, bytesRead);
                accLen += bytesRead;

                // Emit every complete access unit: the bytes between consecutive AUD start codes.
                // Keep the trailing (possibly incomplete) access unit in the accumulator.
                int lastCut = 0;
                var window = acc.AsSpan(0, accLen);

                // From 1, not 0: the accumulator starts AT an AUD, and cutting there would emit an
                // empty access unit.
                // No p <= lastCut guard: the search starts at 1 with lastCut 0 and each subsequent p
                // comes from a search starting at p + 1, so p strictly increases past lastCut. The old
                // loop carried the same test and it was equally dead there.
                for (int p = IndexOfAudStart(window, 1); p >= 0; p = IndexOfAudStart(window, p + 1))
                {
                    var frame = new byte[p - lastCut];
                    Buffer.BlockCopy(acc, lastCut, frame, 0, p - lastCut);
                    QueueFrame(frame);
                    lastCut = p;
                }

                if (lastCut > 0)
                {
                    accLen -= lastCut;
                    Buffer.BlockCopy(acc, lastCut, acc, 0, accLen);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FFmpeg stdout reader loop exited.");
        }
    }

    /// <summary>
    /// Scans an Annex-B access unit for a NAL unit type 5 (IDR slice), checking both 3- and 4-byte
    /// start codes. An access unit containing an IDR is independently decodable — it doesn't
    /// reference any frame that may have been dropped upstream.
    /// </summary>
    internal static bool ContainsIdr(byte[] au) => IndexOfNalOfType(au, 0, IdrNalType) >= 0;

    /// <summary>Annex-B three-byte start code. Also the tail of the four-byte form.</summary>
    private static ReadOnlySpan<byte> StartCode3 => [0x00, 0x00, 0x01];

    /// <summary>Annex-B four-byte start code.</summary>
    private static ReadOnlySpan<byte> StartCode4 => [0x00, 0x00, 0x00, 0x01];

    private const int IdrNalType = 5;
    private const int AudNalType = 9;

    /// <summary>
    /// Index of the next NAL of <paramref name="nalType"/>, at or after <paramref name="from"/>,
    /// behind EITHER start-code form; -1 if there is none.
    /// </summary>
    /// <remarks>
    /// Scanning for the THREE-byte code finds both forms, because <c>00 00 00 01</c> contains
    /// <c>00 00 01</c> starting one byte in — and in both cases the NAL header is the byte straight
    /// after the match. That is why this needs one pass rather than the two interleaved cases the
    /// hand-rolled loop carried.
    ///
    /// <c>IndexOf</c> over a span is vectorized, so the search runs on wide registers instead of a
    /// byte at a time. This runs over every access unit the encoder emits — 120+ a second at the
    /// frame rates this targets, over buffers up to a megabyte — so the scan is not free. (RemEx-rpu2)
    /// </remarks>
    internal static int IndexOfNalOfType(ReadOnlySpan<byte> buffer, int from, int nalType)
    {
        while (from >= 0 && from <= buffer.Length - StartCode3.Length)
        {
            int hit = buffer[from..].IndexOf(StartCode3);
            if (hit < 0) return -1;

            int at = from + hit;
            int header = at + StartCode3.Length;
            if (header < buffer.Length && (buffer[header] & 0x1F) == nalType) return at;

            // Advance by one. Start codes provably cannot overlap — a match at i needs buffer[i+2]
            // to be 1, which is exactly what a match at i+1 or i+2 would need to be 0 — so stepping
            // past the whole match would be equivalent. One byte is simply the step that stays
            // correct if the pattern is ever changed to one that CAN overlap.
            from = at + 1;
        }

        return -1;
    }

    /// <summary>
    /// Index of the next access-unit delimiter introduced by a FOUR-byte start code, at or after
    /// <paramref name="from"/>; -1 if there is none.
    /// </summary>
    /// <remarks>
    /// Four-byte only, deliberately: the encoder is started with <c>-aud 1</c> / x264 <c>aud=1</c>,
    /// which emits the long form, and the reader cuts access units on these boundaries. Accepting the
    /// three-byte form here would cut in places the previous scanner did not, changing how the stream
    /// is framed rather than just how fast it is found.
    /// </remarks>
    internal static int IndexOfAudStart(ReadOnlySpan<byte> buffer, int from)
    {
        while (from >= 0 && from <= buffer.Length - StartCode4.Length)
        {
            int hit = buffer[from..].IndexOf(StartCode4);
            if (hit < 0) return -1;

            int at = from + hit;
            int header = at + StartCode4.Length;
            if (header < buffer.Length && (buffer[header] & 0x1F) == AudNalType) return at;

            from = at + 1;
        }

        return -1;
    }

    // Reader thread only — no locks needed (single writer to _encodedFrames, matching SingleWriter=true).
    private void QueueFrame(byte[] frame)
    {
        if (frame.Length <= 0 || _encodedFrames is null) return;

        if (_droppingUntilIdr)
        {
            if (!ContainsIdr(frame))
            {
                _droppedAccessUnits++;
                return;
            }
            _droppingUntilIdr = false; // resume at an independently decodable access unit
        }

        if (_encodedFrames.Writer.TryWrite(frame))
            return;

        // Output channel overflowed: a gap opened in the access-unit stream, so anything already
        // queued after it references frames the decoder will never see — forwarding that backlog
        // would macroblock rather than recover. Drain it, drop until the next IDR, and request one
        // so the decoder resyncs as soon as possible (the handler throttles reinits to ≤1/5s).
        while (_encodedFrames.Reader.TryRead(out _)) _droppedAccessUnits++;
        _droppedAccessUnits++;
        _droppingUntilIdr = !ContainsIdr(frame);
        if (!_droppingUntilIdr)
            _encodedFrames.Writer.TryWrite(frame);
        RequestKeyframe();

        var now = Environment.TickCount64;
        if (now - _lastDropLogMs > 5000)
        {
            _lastDropLogMs = now;
            _logger.LogWarning(
                "Encoded H.264 output backlog full; dropped {Count} access units total, resuming at next IDR (keyframe requested).",
                _droppedAccessUnits);
        }
    }

    public byte[]? EncodeFrame(ReadOnlyMemory<byte> rawPixelsBGRA, bool forceKeyframe)
    {
        // forceKeyframe is honored at the stream-control layer (RemoteDesktopHandler requests a real
        // on-demand IDR by reinitializing the encoder, which emits fresh SPS/PPS + an IDR). Within a
        // running ffmpeg child reading rawvideo from a pipe there is no per-frame keyframe API, so the
        // flag is intentionally not actioned here; the fps-scaled GOP (ComputeGop) and on-demand
        // reinit cover recovery.

        if (!_initialized || _inputChannel is null || _ffmpegProcess is { HasExited: true })
            return null;

        // Non-blocking submit. If the bounded input channel is full (encoder busy), DROP this frame
        // rather than blocking the capture thread — the freshest frame supersedes a stale backlog and
        // capture/encode must stay decoupled. (RemEx-ii3)
        if (_inputChannel.Writer.TryWrite(rawPixelsBGRA))
        {
            Interlocked.Increment(ref _acceptedInputFrames);
        }

        // Decoupled, non-blocking: return an encoded access unit if one is ready. The encoder
        // pipelines (≈0.5–1s warmup before the first output), so returning null here is normal and
        // must NOT be treated as a capture failure — the reader thread fills the channel continuously.
        return _encodedFrames is not null && _encodedFrames.Reader.TryRead(out var encoded) ? encoded : null;
    }

    /// <summary>
    /// Requests an on-demand IDR. Thread-safe; the capture loop consumes the flag and reinitializes
    /// the encoder, which emits fresh SPS/PPS + a forced IDR for desync recovery. (RemEx-bqc)
    /// </summary>
    public void RequestKeyframe() => Interlocked.Exchange(ref _keyframeRequested, 1);

    /// <summary>
    /// Atomically reads-and-clears the pending keyframe request. Returns true once per request.
    /// </summary>
    public bool ConsumeKeyframeRequest() => Interlocked.Exchange(ref _keyframeRequested, 0) == 1;

    /// <summary>
    /// Non-blocking drain of any additional encoded access units already produced by the encoder,
    /// beyond the one returned by <see cref="EncodeFrame"/>. Lets the caller flush a warmup burst
    /// in order so encoded frames aren't left queued (which would add latency).
    /// </summary>
    public bool TryGetEncodedFrame(out byte[]? frame)
    {
        if (_encodedFrames is not null && _encodedFrames.Reader.TryRead(out var f))
        {
            frame = f;
            return true;
        }

        frame = null;
        return false;
    }

    private static string? FindExecutable(string name)
    {
        try
        {
            var cmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var psi = new ProcessStartInfo(cmd, name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1500);

            // Split by lines in case 'where' returns multiple paths
            var lines = path.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            return proc.ExitCode == 0 && lines.Length > 0 && File.Exists(lines[0]) ? lines[0] : null;
        }
        catch { return null; }
    }

    private void DisposeProcess()
    {
        _initialized = false;
        ActiveCodecName = null;

        // Cancel every async pump first so the stdin writer + stderr reader stop touching the process
        // (and the logger) before we kill it. (RemEx-aa0)
        var cts = _processCts;
        _processCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* best effort */ }
        }

        // Complete the input channel so the writer loop exits its ReadAllAsync even if it wasn't
        // observing cancellation at the await point.
        _inputChannel?.Writer.TryComplete();

        // Give the stdin writer a brief moment to unwind so it doesn't write into a killed process.
        try { _writerTask?.Wait(TimeSpan.FromMilliseconds(250)); }
        catch { /* faulted/cancelled — expected */ }
        _writerTask = null;
        _inputChannel = null;

        if (_ffmpegProcess != null)
        {
            try
            {
                if (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill(entireProcessTree: true);
                }
            }
            catch { /* best effort */ }
            finally
            {
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }
        }

        cts?.Dispose();

        // Drain any remaining encoded frames and drop the channel reference.
        if (_encodedFrames is not null)
        {
            _encodedFrames.Writer.TryComplete();
            while (_encodedFrames.Reader.TryRead(out _)) { }
            _encodedFrames = null;
        }

        _readerThread = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DisposeProcess();
        GC.SuppressFinalize(this);
    }

    ~FFmpegH264Encoder()
    {
        Dispose();
    }
}
