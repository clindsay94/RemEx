using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.RemoteDesktop;

public sealed class FFmpegH264Encoder : IH264Encoder
{
    private readonly ILogger _logger;
    private Process? _ffmpegProcess;
    private Stream? _stdin;
    private Thread? _readerThread;
    private readonly ConcurrentQueue<byte[]> _encodedFrames = new();
    private readonly SemaphoreSlim _frameSemaphore = new(0);
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

        // Clean out any stale frames
        while (_encodedFrames.TryDequeue(out _)) { }
        while (_frameSemaphore.CurrentCount > 0) _frameSemaphore.Wait(0);

        // Hardware-accelerated codecs to try in order of preference
        string[] codecsToTry;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            codecsToTry = new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
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
                case "h264_nvenc":
                    // NVIDIA NVENC. `-tune ll` = low latency (valid values are hq/ll/ull/lossless;
                    // "lowlatency" is NOT valid and makes nvenc fail to start). `-aud 1` emits Access
                    // Unit Delimiters, which the stdout reader relies on to split encoded frames.
                    // `-pix_fmt yuv420p` keeps output to 8-bit 4:2:0 that every H.264 decoder accepts.
                    argsBuilder.Append($"-c:v h264_nvenc -preset p1 -tune ll -rc constqp -qp {qp} -g 60 -aud 1 -pix_fmt yuv420p");
                    break;
                case "h264_vaapi":
                    // VA-API on Linux (Intel/AMD)
                    argsBuilder.Append($"-vaapi_device /dev/dri/renderD128 -vf format=nv12,hwupload -c:v h264_vaapi -qp {qp} -g 60 -aud 1");
                    break;
                case "h264_qsv":
                    // Intel Quick Sync
                    argsBuilder.Append($"-c:v h264_qsv -preset veryfast -look_ahead 0 -global_quality {qp} -g 60 -aud 1");
                    break;
                case "h264_amf":
                    // AMD AMF
                    argsBuilder.Append($"-c:v h264_amf -quality speed -rc cqp -qp_i {qp} -qp_p {qp} -g 60 -aud 1");
                    break;
                default:
                    // libx264 software fallback with zero latency. libx264 has no generic
                    // `-aud` AVOption; AUD emission is requested via x264 params instead.
                    argsBuilder.Append($"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf {qp} -g 60 -x264-params aud=1");
                    break;
            }

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
            _stdin = process.StandardInput.BaseStream;

            // Start reader thread to parse Annex B stream from stdout
            _readerThread = new Thread(() => ReaderLoop(process.StandardOutput.BaseStream))
            {
                Name = "RemexFFmpegH264Reader",
                IsBackground = true
            };
            _readerThread.Start();

            // Track stderr in background to log errors if any
            Task.Run(async () =>
            {
                using var reader = process.StandardError;
                while (!process.HasExited)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null && line.Contains("Error", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("FFmpeg stderr: {Line}", line);
                    }
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to start FFmpeg with codec {Codec}", codec);
            return false;
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

                // Append to accumulator, growing if needed.
                if (accLen + bytesRead > acc.Length)
                {
                    int newSize = Math.Max(acc.Length * 2, accLen + bytesRead);
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
        _encodedFrames.Enqueue(frame);
    }

    public byte[]? EncodeFrame(byte[] rawPixelsBGRA, bool forceKeyframe)
    {
        // Note: forceKeyframe is an intent hint but is NOT actionable here.
        // FFmpeg's stdin-pipe interface doesn't support on-demand keyframe injection
        // without codec-specific signaling. Keyframe interval is governed by the
        // -g (GOP) parameter set during initialization. This is acceptable because
        // the GOP is fixed at 60 frames, providing regular recovery points.

        if (!_initialized || _stdin == null || _ffmpegProcess is { HasExited: true })
            return null;

        try
        {
            // Submit the raw frame to the encoder. Writing may block briefly if the encoder is busy
            // (natural backpressure) — that's fine, it paces capture to the encoder's consumption rate.
            _stdin.Write(rawPixelsBGRA, 0, rawPixelsBGRA.Length);
            _stdin.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error writing frame to FFmpeg stdin.");
            return null;
        }

        // Decoupled, non-blocking: return an encoded access unit if one is ready. The encoder
        // pipelines (≈0.5–1s warmup before the first output), so returning null here is normal and
        // must NOT be treated as a capture failure — the reader thread fills the queue continuously.
        return _encodedFrames.TryDequeue(out var encoded) ? encoded : null;
    }

    /// <summary>
    /// Non-blocking drain of any additional encoded access units already produced by the encoder,
    /// beyond the one returned by <see cref="EncodeFrame"/>. Lets the caller flush a warmup burst
    /// in order so encoded frames aren't left queued (which would add latency).
    /// </summary>
    public bool TryGetEncodedFrame(out byte[]? frame)
    {
        if (_encodedFrames.TryDequeue(out var f))
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
        _stdin = null;
        ActiveCodecName = null;

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

        _readerThread = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DisposeProcess();
        _frameSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    ~FFmpegH264Encoder()
    {
        Dispose();
    }
}
