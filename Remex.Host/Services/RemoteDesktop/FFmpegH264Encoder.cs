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

    public bool Initialize(int width, int height, int fps, int bitrateKbps)
    {
        if (!IsAvailable || _ffmpegPath == null) return false;
        if (_initialized) DisposeProcess();

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
            if (TryStartFFmpeg(codec, width, height, fps, bitrateKbps))
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

    private bool TryStartFFmpeg(string codec, int width, int height, int fps, int bitrateKbps)
    {
        try
        {
            var argsBuilder = new StringBuilder();

            // Input: Raw BGRA frames from stdin
            argsBuilder.Append($"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i - -an ");

            // Codec & Quality Optimization
            switch (codec)
            {
                case "h264_nvenc":
                    // NVIDIA NVENC low latency
                    argsBuilder.Append("-c:v h264_nvenc -preset p1 -tune lowlatency -rc constqp -qp 28 -g 60 -write_aud 1");
                    break;
                case "h264_vaapi":
                    // VA-API on Linux (Intel/AMD)
                    argsBuilder.Append("-vaapi_device /dev/dri/renderD128 -vf format=nv12,hwupload -c:v h264_vaapi -qp 28 -g 60 -write_aud 1");
                    break;
                case "h264_qsv":
                    // Intel Quick Sync
                    argsBuilder.Append("-c:v h264_qsv -preset veryfast -look_ahead 0 -g 60 -write_aud 1");
                    break;
                case "h264_amf":
                    // AMD AMF
                    argsBuilder.Append("-c:v h264_amf -quality speed -rc cqp -qp_i 28 -qp_p 28 -g 60 -write_aud 1");
                    break;
                default:
                    // libx264 software fallback with zero latency
                    argsBuilder.Append("-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf 28 -g 60 -write_aud 1");
                    break;
            }

            // Output raw Annex B H.264 stream to stdout
            argsBuilder.Append(" -f h264 -");

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

            // Wait brief moment to see if it immediately exits (indicating codec unsupported)
            if (process.WaitForExit(150))
            {
                var error = process.StandardError.ReadToEnd();
                _logger.LogDebug("FFmpeg codec {Codec} unsupported: {Error}", codec, error);
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
        var readBuffer = new byte[1024 * 128];
        var frameBuffer = new MemoryStream();
        var startCodeBuffer = new byte[4];
        int startCodeCount = 0;

        try
        {
            while (!_isDisposed && _ffmpegProcess is { HasExited: false })
            {
                int bytesRead = stdout.Read(readBuffer, 0, readBuffer.Length);
                if (bytesRead <= 0) break;

                int i = 0;
                while (i < bytesRead)
                {
                    byte b = readBuffer[i];

                    // Standard Annex B start code parser: look for 0x00 0x00 0x00 0x01
                    if (b == 0x00)
                    {
                        startCodeCount++;
                    }
                    else if (b == 0x01 && startCodeCount >= 2)
                    {
                        // Found a start code!
                        int startCodeLen = startCodeCount >= 3 ? 4 : 3;

                        // Slice the current frame buffer and queue it if it contains data
                        if (frameBuffer.Length > (long)startCodeLen)
                        {
                            // Capture the frame bytes excluding the next start code we just scanned
                            long frameLen = frameBuffer.Length - startCodeLen;
                            var frameBytes = new byte[frameLen];
                            Array.Copy(frameBuffer.GetBuffer(), 0, frameBytes, 0, frameLen);

                            // Inspect if this contains an AUD (type 9) NAL unit to split frames
                            // AUD NAL unit usually begins after the start code
                            int nalIndex = 0;
                            while (nalIndex < frameBytes.Length - 4)
                            {
                                if (frameBytes[nalIndex] == 0x00 && frameBytes[nalIndex + 1] == 0x00 &&
                                    frameBytes[nalIndex + 2] == 0x00 && frameBytes[nalIndex + 3] == 0x01)
                                {
                                    int type = frameBytes[nalIndex + 4] & 0x1F;
                                    if (type == 9) // Access Unit Delimiter (AUD)
                                    {
                                        // This indicates a frame boundary. Queue the previous frame data
                                        if (nalIndex > 0)
                                        {
                                            var completeFrame = new byte[nalIndex];
                                            Array.Copy(frameBytes, 0, completeFrame, 0, nalIndex);
                                            QueueFrame(completeFrame);

                                            // Shift the remaining NAL units to the start
                                            var remainingLen = frameBytes.Length - nalIndex;
                                            var temp = new byte[remainingLen];
                                            Array.Copy(frameBytes, nalIndex, temp, 0, remainingLen);
                                            frameBytes = temp;
                                        }
                                        break;
                                    }
                                }
                                nalIndex++;
                            }

                            // Write the current frame bytes to buffer
                            frameBuffer.SetLength(0);
                            for (int k = 0; k < startCodeLen; k++)
                                frameBuffer.WriteByte(0x00);
                            frameBuffer.WriteByte(0x01);
                        }

                        startCodeCount = 0;
                    }
                    else
                    {
                        startCodeCount = 0;
                    }

                    frameBuffer.WriteByte(b);
                    i++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FFmpeg stdout reader loop exited.");
        }
        finally
        {
            frameBuffer.Dispose();
        }
    }

    private void QueueFrame(byte[] frame)
    {
        if (frame.Length <= 0) return;
        _encodedFrames.Enqueue(frame);
        _frameSemaphore.Release();
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
            // Write raw frame pixels to stdin
            _stdin.Write(rawPixelsBGRA, 0, rawPixelsBGRA.Length);
            _stdin.Flush();

            // Wait for the next complete frame in the queue
            // A 150ms timeout ensures we don't block the hot loop forever if FFmpeg lags
            if (_frameSemaphore.Wait(150))
            {
                if (_encodedFrames.TryDequeue(out var frame))
                {
                    return frame;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error writing frame to FFmpeg stdin.");
        }

        return null;
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
