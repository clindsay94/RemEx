using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Remex.Agent.Services.RemoteDesktop;

public sealed class FFmpegH264Encoder : IH264Encoder
{
    private readonly ILogger _logger;
    private Process? _ffmpegProcess;

    // Bounded, drop-newest feed from the capture thread to the dedicated stdin writer. A tiny
    // capacity (a few frames) is intentional: if the encoder can't keep up, the freshest frames
    // matter, not a backlog, so EncodeFrame drops rather than blocking the capture thread. (RemEx-ii3)
    private const int InputChannelCapacity = 3;
    private Channel<byte[]>? _inputChannel;
    private Task? _writerTask;

    // Bounded, drop-oldest output of encoded Annex-B access units. Replaces an unbounded queue so a
    // stalled/slow sender can't grow memory without limit; oldest frames are evicted first because a
    // newer frame supersedes them for a real-time stream. (RemEx-fs5)
    private const int OutputChannelCapacity = 8;
    private Channel<byte[]>? _encodedFrames;

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

    private bool _isDisposed;
    private string? _ffmpegPath;
    private int _width;
    private int _height;
    private bool _initialized;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Exact raw BGRA byte count this encoder expects per frame (width * height * 4).
    /// 0 until <see cref="Initialize"/> succeeds.
    /// </summary>
    public int ExpectedInputByteCount => _initialized ? _width * _height * 4 : 0;

    /// <summary>
    /// The FFmpeg codec string that was successfully started (e.g. "h264_nvenc", "libx264").
    /// Null if not yet initialized.
    /// </summary>
    public string? ActiveCodecName { get; private set; }

    public FFmpegH264Encoder(ILogger logger)
    {
        _logger = logger;
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

    public bool Initialize(int width, int height, int fps, int qp)
    {
        if (!IsAvailable || _ffmpegPath == null) return false;
        if (_initialized) DisposeProcess();

        // Constant-QP rate control. Lower QP = higher quality + bitrate. Clamp to a sane H.264 range.
        qp = Math.Clamp(qp, 16, 45);

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
            codecsToTry = new[] { "h264_vaapi", "h264_nvenc", "libx264" };
        }

        foreach (var codec in codecsToTry)
        {
            if (TryStartFFmpeg(codec, width, height, fps, qp))
            {
                _logger.LogInformation("FFmpeg H.264 encoder successfully initialized using codec: {Codec}", codec);
                ActiveCodecName = codec;
                _initialized = true;
                return true;
            }
        }

        _logger.LogError("Failed to start FFmpeg with any H.264 encoder codec.");
        return false;
    }

    private bool TryStartFFmpeg(string codec, int width, int height, int fps, int qp)
    {
        try
        {
            var argsBuilder = new StringBuilder();

            // Input: Raw BGRA frames from stdin
            argsBuilder.Append($"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i - -an ");

            // Codec & Quality Optimization. qp drives constant-QP rate control (lower = better quality).
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
                    argsBuilder.Append($"-c:v h264_nvenc -preset p1 -tune ll -rc constqp -qp {qp} -g 60 -forced-idr 1 -aud 1");
                    break;
                case "h264_nvenc":
                    // NVIDIA NVENC. `-tune ll` = low latency (valid values are hq/ll/ull/lossless;
                    // "lowlatency" is NOT valid and makes nvenc fail to start). `-aud 1` emits Access
                    // Unit Delimiters, which the stdout reader relies on to split encoded frames.
                    // `-pix_fmt yuv420p` keeps output to 8-bit 4:2:0 that every H.264 decoder accepts.
                    // `-forced-idr 1` makes the forced keyframes emitted by `-force_key_frames` true
                    // IDR frames (with fresh SPS/PPS) instead of plain non-IDR I-frames, so an
                    // on-demand keyframe is independently decodable by a desynced client. (RemEx-bqc)
                    argsBuilder.Append($"-c:v h264_nvenc -preset p1 -tune ll -rc constqp -qp {qp} -g 60 -forced-idr 1 -aud 1 -pix_fmt yuv420p");
                    break;
                case "h264_vaapi":
                    // VA-API on Linux (Intel/AMD)
                    argsBuilder.Append($"-vaapi_device /dev/dri/renderD128 -vf format=nv12,hwupload -c:v h264_vaapi -qp {qp} -g 60 -aud 1");
                    break;
                case "h264_qsv":
                    // Intel Quick Sync
                    argsBuilder.Append($"-c:v h264_qsv -preset veryfast -look_ahead 0 -global_quality {qp} -g 60 -forced-idr 1 -aud 1");
                    break;
                case "h264_amf":
                    // AMD AMF
                    argsBuilder.Append($"-c:v h264_amf -quality speed -rc cqp -qp_i {qp} -qp_p {qp} -g 60 -forced-idr 1 -aud 1");
                    break;
                default:
                    // libx264 software fallback with zero latency. libx264 has no generic
                    // `-aud` AVOption; AUD emission is requested via x264 params instead. x264 always
                    // makes a forced keyframe a true IDR, so no extra flag is needed for it.
                    argsBuilder.Append($"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf {qp} -g 60 -x264-params aud=1");
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
            argsBuilder.Append(" -flush_packets 1 -f h264 -");

            var psi = new ProcessStartInfo(_ffmpegPath!, argsBuilder.ToString())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process == null) return false;

            // Decide success/failure by watching for early process exit.
            //
            // Two distinct failure modes must both be caught here, otherwise the codec-fallback
            // loop is skipped and we silently end up with a dead encoder (→ MJPEG fallback):
            //   1. Codec not built into ffmpeg / no device → ffmpeg exits almost immediately
            //      ("Unknown encoder", "Cannot load nvcuda").
            //   2. Hardware encoder present but rejects the parameters (resolution above the
            //      NVENC max, unsupported framerate, etc.) → ffmpeg starts, then fails a few
            //      hundred ms later "while opening encoder" and exits non-zero. A 150 ms window
            //      missed this, so it was reported as success.
            // A healthy rawvideo-pipe encoder never exits on its own (it blocks waiting for stdin
            // frames), so an exit within this window unambiguously means failure. The cost is a
            // one-time startup wait for the codec that actually succeeds.
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
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true,
            });

            _inputChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(InputChannelCapacity)
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
    private async Task StdinWriterLoop(Stream stdin, ChannelReader<byte[]> reader, CancellationToken ct)
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
                int limit = accLen - 5; // need 5 bytes to test 00 00 00 01 <nal>
                for (int p = 1; p <= limit; p++)
                {
                    if (acc[p] == 0x00 && acc[p + 1] == 0x00 && acc[p + 2] == 0x00 && acc[p + 3] == 0x01 &&
                        (acc[p + 4] & 0x1F) == 9)
                    {
                        if (p > lastCut)
                        {
                            var frame = new byte[p - lastCut];
                            Buffer.BlockCopy(acc, lastCut, frame, 0, p - lastCut);
                            QueueFrame(frame);
                            lastCut = p;
                        }
                    }
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

    private void QueueFrame(byte[] frame)
    {
        if (frame.Length <= 0) return;
        // Drop-oldest bounded channel: TryWrite always succeeds, evicting the oldest queued access
        // unit when full so memory stays bounded under a slow consumer. (RemEx-fs5)
        _encodedFrames?.Writer.TryWrite(frame);
    }

    public byte[]? EncodeFrame(byte[] rawPixelsBGRA, bool forceKeyframe)
    {
        // forceKeyframe is honored at the stream-control layer (RemoteDesktopHandler requests a real
        // on-demand IDR by reinitializing the encoder, which emits fresh SPS/PPS + an IDR). Within a
        // running ffmpeg child reading rawvideo from a pipe there is no per-frame keyframe API, so the
        // flag is intentionally not actioned here; the GOP (-g 60) and on-demand reinit cover recovery.

        if (!_initialized || _inputChannel is null || _ffmpegProcess is { HasExited: true })
            return null;

        // Non-blocking submit. If the bounded input channel is full (encoder busy), DROP this frame
        // rather than blocking the capture thread — the freshest frame supersedes a stale backlog and
        // capture/encode must stay decoupled. (RemEx-ii3)
        _inputChannel.Writer.TryWrite(rawPixelsBGRA);

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
