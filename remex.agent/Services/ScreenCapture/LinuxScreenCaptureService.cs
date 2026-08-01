using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Agent.Services.RemoteDesktop.Linux.Capture;

namespace Remex.Agent.Services.ScreenCapture;

[SupportedOSPlatform("linux")]
public class LinuxScreenCaptureService : IScreenCaptureService
{
    private static readonly Regex XrandrGeometryRegex = new(
        @"^(?<width>\d+)x(?<height>\d+)(?<x>[+-]\d+)(?<y>[+-]\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<LinuxScreenCaptureService> _logger;
    private int _screenWidth;
    private int _screenHeight;
    private int _screenLeft;
    private int _screenTop;
    // Primary monitor geometry — used to crop multi-monitor captures
    private int _primaryX;
    private int _primaryY;
    private int _primaryWidth;
    private int _primaryHeight;
    private int _activeLeft;
    private int _activeTop;
    private int _activeWidth;
    private int _activeHeight;
    private DesktopCaptureTarget _activeTarget = new() { CaptureMode = DesktopCaptureMode.VirtualDesktop };
    private List<DesktopDisplayInfo> _detectedDisplays = [];
    private readonly object _targetSync = new();

    // Stage 2: PipeWire capture coordinator. Injected externally when the
    // WaylandNative or PortalNoPen tier is active; null for legacy (X11/fallback) path.
    // volatile ensures the reference is visible across threads without a lock.
    private volatile LinuxCaptureSessionCoordinator? _captureCoordinator;

    // Cache of the last successfully captured JPEG frame for reuse on static screens/timeouts.
    // Guarded by the same capture-target signature as the raw cache: replaying a JPEG from a
    // previous monitor/scale after a target switch would show the wrong screen. (RemEx-lq6h)
    private sealed record JpegFrameCache(
        byte[] Bytes, int ActiveLeft, int ActiveTop, int ActiveWidth, int ActiveHeight, double Scale);
    private JpegFrameCache? _lastJpegFrame;

    private enum DisplayServer { Unknown, X11, Wayland }
    private readonly DisplayServer _displayServer;
    private readonly string _display; // $DISPLAY for X11, $WAYLAND_DISPLAY for Wayland
    private readonly string? _captureToolPath;
    private readonly string? _fallbackToolPath;

    public LinuxScreenCaptureService(ILogger<LinuxScreenCaptureService> logger)
    {
        _logger = logger;
        _displayServer = DetectDisplayServer();
        _display = _displayServer switch
        {
            DisplayServer.Wayland => Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0",
            DisplayServer.X11 => Environment.GetEnvironmentVariable("DISPLAY") ?? ":0",
            _ => ":0"
        };

        (_captureToolPath, _fallbackToolPath) = DetectCaptureTools();
        DetectScreenSize();

        _logger.LogInformation(
            "Linux screen capture initialized: display={Display}, server={Server}, " +
            "resolution={W}x{H}, primaryTool={Tool}, fallback={Fallback}",
            _display, _displayServer, _screenWidth, _screenHeight,
            _captureToolPath ?? "none", _fallbackToolPath ?? "none");
    }

    public string? BackendName => _captureCoordinator is not null ? "pipewire" : null;

    /// <summary>
    /// Sets the PipeWire capture coordinator to use for the native Wayland path.
    /// Called by the remote desktop handler after the portal session is established.
    /// Pass null to fall back to the legacy shell-tool path.
    /// </summary>
    public void SetCaptureCoordinator(LinuxCaptureSessionCoordinator? coordinator)
    {
        _captureCoordinator = coordinator;
        if (coordinator is not null)
        {
            lock (_targetSync)
            {
                var realDisplays = _detectedDisplays.Where(d => d.DisplayId != "default").ToList();

                // A monitor target that was already applied (client's explicit choice, possibly set
                // by DesktopStart before the portal session finished opening, or carried over from
                // the previous connection) must survive the session (re)open — resetting it here
                // silently reverted the stream to the primary monitor on every reconnect. Only
                // re-default when the chosen display no longer exists. (RemEx-lq6h)
                if (_activeTarget.CaptureMode == DesktopCaptureMode.Monitor &&
                    !string.IsNullOrWhiteSpace(_activeTarget.DisplayId))
                {
                    var existing = realDisplays.FirstOrDefault(d =>
                        string.Equals(d.DisplayId, _activeTarget.DisplayId, StringComparison.Ordinal));
                    if (existing is not null)
                    {
                        _activeLeft = existing.Left;
                        _activeTop = existing.Top;
                        _activeWidth = existing.Width;
                        _activeHeight = existing.Height;
                        return;
                    }
                }

                // Default a fresh PipeWire session to the primary monitor when more than one display
                // exists: a single ~2560x1440 crop stays under the 4096px H.264 limit and encodes on
                // NVENC without a CPU downscale, so the stream is fast out of the box. The client can
                // still switch to another monitor or "both screens" (VirtualDesktop). (RemEx-nadp)
                var primary = realDisplays.FirstOrDefault(d => d.IsPrimary) ?? realDisplays.FirstOrDefault();

                if (realDisplays.Count > 1 && primary is not null)
                {
                    _activeTarget = new DesktopCaptureTarget
                    {
                        CaptureMode = DesktopCaptureMode.Monitor,
                        DisplayId = primary.DisplayId,
                    };
                    _activeLeft = primary.Left;
                    _activeTop = primary.Top;
                    _activeWidth = primary.Width;
                    _activeHeight = primary.Height;
                }
                else
                {
                    _activeTarget = new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.VirtualDesktop };
                    _activeLeft = _screenLeft;
                    _activeTop = _screenTop;
                    _activeWidth = _screenWidth;
                    _activeHeight = _screenHeight;
                }
            }
        }
    }

    // Last successfully encoded raw frame plus the capture geometry/scale it was produced under.
    // A cached frame may only be replayed while the active target and scale are UNCHANGED: the
    // H.264 encoder's ffmpeg input size is fixed at creation, and replaying a frame from a
    // previous target (e.g. right after a monitor switch) desyncs the rawvideo pipe and throws
    // the encoder into a reinit storm. Stored as one reference so reads are atomic. (RemEx-lq6h)
    // The offset is part of the signature: two monitors can share identical dimensions, and a
    // replay from the previous monitor would otherwise pass the size check while showing the
    // wrong screen's content after a target switch.
    private sealed record RawFrameCache(
        byte[] Bytes, int ActiveLeft, int ActiveTop, int ActiveWidth, int ActiveHeight, double Scale);
    private RawFrameCache? _lastRawFrame;

    public async Task<byte[]?> CaptureRawScreenAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
    {
        var (bytes, _) = await CaptureRawCoreAsync(scale, drawCursor, ct);
        return bytes;
    }

    /// <summary>
    /// Liveness-aware raw capture (parity with the Windows implementation, RemEx-ltd): a replay of
    /// the cached frame on a healthy static screen stays IsLive = true (PipeWire is damage-driven,
    /// so an unchanged screen legitimately delivers no frames), but a geometry-stale cache — the
    /// active target or scale changed since it was encoded — is never replayed at all.
    /// </summary>
    public async Task<ScreenCaptureResult> CaptureRawScreenLiveAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
    {
        var (bytes, isLive) = await CaptureRawCoreAsync(scale, drawCursor, ct);
        return new ScreenCaptureResult(bytes, isLive);
    }

    private async Task<(byte[]? Bytes, bool IsLive)> CaptureRawCoreAsync(double scale, bool drawCursor, CancellationToken ct)
    {
        scale = Math.Clamp(scale, 0.25, 1.0);

        // ── Stage 2 fast path: PipeWire native capture ─────────────────
        if (_captureCoordinator is { IsRunning: true })
        {
            int activeL, activeT, activeW, activeH;
            lock (_targetSync)
            {
                activeL = _activeLeft;
                activeT = _activeTop;
                activeW = _activeWidth;
                activeH = _activeHeight;
            }

            try
            {
                var frame = await _captureCoordinator.WaitForNextFrameAsync(timeoutMs: 80, ct: ct);
                if (frame is not null)
                {
                    try
                    {
                        TryGetActiveCrop(frame.Width, frame.Height, out var cx, out var cy, out var cw, out var ch);
                        var raw = EncodeRaw(frame, scale, _logger, cx, cy, cw, ch);
                        if (raw is { Length: > 0 })
                        {
                            _lastRawFrame = new RawFrameCache(raw, activeL, activeT, activeW, activeH, scale);
                            return (raw, true);
                        }
                    }
                    finally
                    {
                        if (frame.Data is not null)
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(frame.Data);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PipeWire raw capture loop encountered an error.");
            }

            // No fresh frame this tick — normal for a static screen on damage-driven PipeWire.
            // Replay the cache only while its geometry/scale still match the active target.
            var cache = _lastRawFrame;
            if (cache is not null &&
                cache.ActiveLeft == activeL && cache.ActiveTop == activeT &&
                cache.ActiveWidth == activeW && cache.ActiveHeight == activeH &&
                Math.Abs(cache.Scale - scale) < 0.0001)
            {
                return (cache.Bytes, true);
            }

            return (null, false);
        }

        // ── Fallback path (maim/spectacle) ───────────────────────────
        try
        {
            var jpegBytes = await CaptureScreenAsync(50, scale, drawCursor, ct);
            if (jpegBytes is { Length: > 0 })
            {
                // Wrap the frame in place rather than copying it out: ToArray here would
                // reintroduce, on Linux, exactly the per-frame frame-sized copy RemEx-hgox removed.
                // The fallback covers a memory that is not array-backed, which cannot happen today.
                using var ms = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(jpegBytes, out var jpegSegment)
                    && jpegSegment.Array is { } jpegArray
                        ? new MemoryStream(jpegArray, jpegSegment.Offset, jpegSegment.Count, writable: false)
                        : new MemoryStream(jpegBytes.ToArray());
                using var bmp = new System.Drawing.Bitmap(ms);
                var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int bytesCount = Math.Abs(bmpData.Stride) * bmp.Height;
                    byte[] bgraValues = new byte[bytesCount];
                    System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, bgraValues, 0, bytesCount);
                    return (bgraValues, true);
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Linux fallback raw capture failed.");
        }

        return (null, false);
    }

    private static byte[] EncodeRaw(LinuxFrameSnapshot frame, double scale, ILogger logger, int cropX, int cropY, int cropW, int cropH)
    {
        System.Runtime.InteropServices.GCHandle pinHandle = default;
        try
        {
            var colorType = LinuxJpegEncoder.MapFormat(frame.Format, logger, out var formatTag);
            if (colorType == SkiaSharp.SKColorType.Unknown)
                return Array.Empty<byte>();

            var info = new SkiaSharp.SKImageInfo(frame.Width, frame.Height, colorType, SkiaSharp.SKAlphaType.Premul);

            SkiaSharp.SKImage? image;
            if (frame.Data is not null)
            {
                pinHandle = System.Runtime.InteropServices.GCHandle.Alloc(frame.Data, System.Runtime.InteropServices.GCHandleType.Pinned);
                var ptr = pinHandle.AddrOfPinnedObject();
                image = SkiaSharp.SKImage.FromPixels(info, ptr, frame.Stride);
            }
            else if (frame.RawData != IntPtr.Zero)
            {
                image = SkiaSharp.SKImage.FromPixels(info, frame.RawData, frame.Stride);
            }
            else
            {
                return Array.Empty<byte>();
            }

            if (image is null) return Array.Empty<byte>();

            // Crop to the active monitor rect before scaling/encoding: shrinks per-frame work and, for a
            // single ~2560x1440 monitor, keeps the surface under the 4096px H.264 limit so NVENC needs no
            // CPU downscale. cropW<=0 means "no crop" (full virtual desktop). (RemEx-nadp)
            SkiaSharp.SKImage? cropped = (cropW > 0 && cropH > 0)
                ? image.Subset(SkiaSharp.SKRectI.Create(cropX, cropY, cropW, cropH))
                : null;

            using (image)
            using (cropped)
            {
                SkiaSharp.SKImage baseImage = cropped ?? image;
                int baseW = baseImage.Width;
                int baseH = baseImage.Height;

                SkiaSharp.SKImage finalImage = baseImage;
                SkiaSharp.SKBitmap? scaledBitmap = null;

                // The H.264 encoder's fixed ffmpeg input size is CaptureScaling.ScaledEven(activeW/H,
                // scale) — see RemoteDesktopHandler.TryCreateH264Encoder. The raw buffer produced here
                // MUST match that size byte-for-byte or the rawvideo pipe desyncs and the encoder
                // reinitializes endlessly. Use the same even-aligned rounding, including at scale 1.0
                // where an odd-sized monitor/crop would otherwise yield odd dimensions that no H.264
                // encoder accepts. (RemEx-lq6h)
                int targetW = CaptureScaling.ScaledEven(baseW, scale);
                int targetH = CaptureScaling.ScaledEven(baseH, scale);

                if (targetW != baseW || targetH != baseH)
                {
                    var destInfo = new SkiaSharp.SKImageInfo(targetW, targetH, colorType, SkiaSharp.SKAlphaType.Premul);
                    using var srcBitmap = SkiaSharp.SKBitmap.FromImage(baseImage);
                    scaledBitmap = new SkiaSharp.SKBitmap(destInfo);
                    if (srcBitmap.ScalePixels(scaledBitmap, SkiaSharp.SKFilterQuality.Medium))
                    {
                        finalImage = SkiaSharp.SKImage.FromBitmap(scaledBitmap);
                    }
                    else
                    {
                        scaledBitmap.Dispose();
                        scaledBitmap = null;
                    }
                }

                try
                {
                    using var srcBmp = SkiaSharp.SKBitmap.FromImage(finalImage);
                    if (srcBmp.ColorType == SkiaSharp.SKColorType.Bgra8888)
                    {
                        return srcBmp.Bytes;
                    }
                    else
                    {
                        using var bgraBmp = srcBmp.Copy(SkiaSharp.SKColorType.Bgra8888);
                        return bgraBmp?.Bytes ?? Array.Empty<byte>();
                    }
                }
                finally
                {
                    if (scaledBitmap is not null)
                    {
                        finalImage.Dispose();
                        scaledBitmap.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Linux raw encoding failed.");
            return Array.Empty<byte>();
        }
        finally
        {
            if (pinHandle.IsAllocated) pinHandle.Free();
        }
    }

    public async Task<ReadOnlyMemory<byte>> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
    {
        quality = Math.Clamp(quality, 1, 100);
        scale = Math.Clamp(scale, 0.25, 1.0);
        var (sourceWidth, sourceHeight, _, _) = GetScreenSize();

        // ── Stage 2 fast path: PipeWire native capture ─────────────────
        if (_captureCoordinator is { IsRunning: true })
        {
            int activeL, activeT, activeW, activeH;
            lock (_targetSync)
            {
                activeL = _activeLeft;
                activeT = _activeTop;
                activeW = _activeWidth;
                activeH = _activeHeight;
            }

            try
            {
                var frame = await _captureCoordinator.WaitForNextFrameAsync(timeoutMs: 80, ct: ct);
                if (frame is not null)
                {
                    try
                    {
                        TryGetActiveCrop(frame.Width, frame.Height, out var jcx, out var jcy, out var jcw, out var jch);
                        var jpeg = LinuxJpegEncoder.Encode(
                            frame, quality, scale, _logger, out var formatTag, jcx, jcy, jcw, jch);
                        if (jpeg.Length > 0)
                        {
                            _lastJpegFrame = new JpegFrameCache(jpeg, activeL, activeT, activeW, activeH, scale);
                            return jpeg;
                        }
                        _logger.LogDebug(
                            "PipeWire frame produced but JPEG encode returned empty " +
                            "(format={Format}); falling back this tick.", formatTag);
                    }
                    finally
                    {
                        if (frame.Data is not null)
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(frame.Data);
                        }
                    }
                }
                else
                {
                    // PipeWire timeout: screen is unchanged. Reuse the cached JPEG only while the
                    // capture target and scale are unchanged (a stale-target replay would show the
                    // previous monitor's content after a switch).
                    var cache = _lastJpegFrame;
                    if (cache is not null &&
                        cache.ActiveLeft == activeL && cache.ActiveTop == activeT &&
                        cache.ActiveWidth == activeW && cache.ActiveHeight == activeH &&
                        Math.Abs(cache.Scale - scale) < 0.0001)
                    {
                        return cache.Bytes;
                    }
                    _logger.LogDebug(
                        "PipeWire frame not available and no usable cached frame; falling back to legacy capture.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PipeWire capture threw; falling back to legacy for this tick.");
            }
        }

        // ── Legacy path: shell tools (spectacle / grim / scrot / ffmpeg) ──

        var tmpFile = Path.Combine(Path.GetTempPath(), $"remex_capture_{Guid.NewGuid():N}.jpg");
        try
        {
            int captureWidth = Math.Max(1, (int)(sourceWidth * scale));
            int captureHeight = Math.Max(1, (int)(sourceHeight * scale));
            int result = -1;

            // Strategy 1: Use detected primary tool
            if (_captureToolPath is not null)
            {
                result = _displayServer switch
                {
                    DisplayServer.Wayland => await CaptureWaylandAsync(_captureToolPath, tmpFile, quality, captureWidth, captureHeight, ct),
                    _ => await CaptureX11Async(_captureToolPath, tmpFile, quality, scale, captureWidth, captureHeight, ct)
                };
            }

            // Strategy 2: Use fallback tool
            if (result != 0 && _fallbackToolPath is not null)
            {
                result = await CaptureWithFfmpegAsync(tmpFile, captureWidth, captureHeight, quality, ct);
            }

            // Strategy 3: Last resort — try gnome-screenshot (works on both X11 and Wayland under GNOME)
            if (result != 0)
            {
                result = await RunProcessAsync("gnome-screenshot", $"-f \"{tmpFile}\"", ct);
            }

            if (result != 0 || !File.Exists(tmpFile))
            {
                _logger.LogWarning("Screen capture failed on Linux (display={Display}, server={Server}).",
                    _display, _displayServer);
                return Array.Empty<byte>();
            }

            var bytes = await File.ReadAllBytesAsync(tmpFile, ct);
            lock (_targetSync)
            {
                _lastJpegFrame = new JpegFrameCache(
                    bytes, _activeLeft, _activeTop, _activeWidth, _activeHeight, scale);
            }
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture screen on Linux.");
            return Array.Empty<byte>();
        }
        finally
        {
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    public (int Width, int Height, int Left, int Top) GetScreenSize() =>
        (_activeWidth, _activeHeight, _activeLeft, _activeTop);

    /// <summary>
    /// Bounding box of the whole virtual desktop (all outputs), independent of the active capture
    /// target. Used to map virtual-desktop pointer coordinates onto the portal ScreenCast stream
    /// for absolute input injection. (RemEx-lq6h)
    /// </summary>
    public (int Left, int Top, int Width, int Height) GetVirtualDesktopBounds() =>
        (_screenLeft, _screenTop, _screenWidth, _screenHeight);

    public DesktopDisplayCatalog GetDisplayCatalog()
    {
        RefreshDisplayTopology();

        // Per-monitor capture is available whenever we enumerated real outputs (kscreen-doctor on KDE,
        // xrandr on X11). The full virtual-desktop frame is captured once and cropped to the selected
        // monitor, so Monitor mode works on Wayland and mid-session too — no dependency on the display
        // server or on an active PipeWire session. (RemEx-nadp)
        var hasRealDisplays = _detectedDisplays.Count > 0 &&
            !(_detectedDisplays.Count == 1 && _detectedDisplays[0].DisplayId == "default");

        if (!hasRealDisplays)
        {
            return new DesktopDisplayCatalog
            {
                DisplayListVersion = ComputeDisplayListVersion(_detectedDisplays),
                SupportedCaptureModes = [DesktopCaptureMode.VirtualDesktop],
                Displays = _detectedDisplays.Count > 0 ? _detectedDisplays.ToArray() : [CreateFallbackDisplay()],
            };
        }

        return new DesktopDisplayCatalog
        {
            DisplayListVersion = ComputeDisplayListVersion(_detectedDisplays),
            SupportedCaptureModes = [DesktopCaptureMode.VirtualDesktop, DesktopCaptureMode.Monitor],
            Displays = _detectedDisplays.ToArray(),
        };
    }

    public bool TrySetCaptureTarget(DesktopCaptureTarget target, out string? error)
    {
        RefreshDisplayTopology();

        lock (_targetSync)
        {
            if (target.CaptureMode == DesktopCaptureMode.VirtualDesktop)
            {
                SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
                _activeTarget = new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.VirtualDesktop };
                error = null;
                return true;
            }

            // Monitor mode: crop the captured virtual-desktop frame to this output's rect. Works on any
            // display server and mid-session because the crop is applied post-capture (EncodeRaw /
            // LinuxJpegEncoder read the active bounds set here). (RemEx-nadp)
            var display = _detectedDisplays.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayId, target.DisplayId, StringComparison.Ordinal));
            if (display is null)
            {
                error = "Unknown display.";
                return false;
            }

            SetActiveBounds(display.Width, display.Height, display.Left, display.Top);
            _activeTarget = new DesktopCaptureTarget
            {
                CaptureMode = DesktopCaptureMode.Monitor,
                DisplayId = display.DisplayId,
            };
            error = null;
            return true;
        }
    }

    private void RefreshDisplayTopology()
    {
        DetectScreenSize();

        lock (_targetSync)
        {
            // A Monitor target must survive a topology refresh even during an active PipeWire session
            // (the crop is applied post-capture), so do NOT reset to VirtualDesktop just because the
            // coordinator is running. Only fall back when the active target really is the full desktop
            // or its display no longer exists. (RemEx-nadp)
            if (_activeTarget.CaptureMode == DesktopCaptureMode.VirtualDesktop ||
                string.IsNullOrWhiteSpace(_activeTarget.DisplayId))
            {
                SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
                _activeTarget = new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.VirtualDesktop };
                return;
            }

            var activeDisplay = _detectedDisplays.FirstOrDefault(display =>
                string.Equals(display.DisplayId, _activeTarget.DisplayId, StringComparison.Ordinal));
            if (activeDisplay is null)
            {
                SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
                _activeTarget = new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.VirtualDesktop };
                return;
            }

            SetActiveBounds(activeDisplay.Width, activeDisplay.Height, activeDisplay.Left, activeDisplay.Top);
        }
    }

    private void SetActiveBounds(int width, int height, int left, int top)
    {
        _activeWidth = width;
        _activeHeight = height;
        _activeLeft = left;
        _activeTop = top;
    }

    private DesktopDisplayInfo CreateFallbackDisplay() =>
        CreateFallbackDisplay(_screenLeft, _screenTop, _screenWidth, _screenHeight);

    /// <summary>
    /// The single-display descriptor used when the host cannot enumerate its outputs.
    /// </summary>
    /// <remarks>
    /// Static and shared by all three fallback sites, which each carried their own copy of this
    /// literal — which is how they came to disagree with the documented contract in the first place.
    /// <see cref="DesktopDisplayInfo.PersistentDisplayKey"/> is EMPTY, not <c>"default"</c>: this
    /// branch established no stable identity and must not claim one. <c>DisplayId</c> stays
    /// <c>"default"</c> because selection needs it, and because <c>GetDisplayCatalog</c> keys off that
    /// exact sentinel to decide the host has no real displays to advertise. (RemEx-kiy1)
    /// </remarks>
    internal static DesktopDisplayInfo CreateFallbackDisplay(int left, int top, int width, int height) => new()
    {
        DisplayId = "default",
        PersistentDisplayKey = string.Empty,
        Name = "Display",
        IsPrimary = true,
        Left = left,
        Top = top,
        Width = width,
        Height = height,
    };

    /// <summary>
    /// Computes the crop rectangle (within a captured full virtual-desktop frame of the given size) for
    /// the active capture target. Returns false — with all outs zeroed — when no crop is needed (the
    /// active target is the whole desktop, or the bounds are unusable), in which case the encoders use
    /// the full frame. Cropping to the selected monitor is what shrinks the per-frame surface. (RemEx-nadp)
    /// </summary>
    private bool TryGetActiveCrop(int frameWidth, int frameHeight, out int x, out int y, out int w, out int h)
    {
        int ax, ay, aw, ah;
        lock (_targetSync)
        {
            ax = _activeLeft - _screenLeft;
            ay = _activeTop - _screenTop;
            aw = _activeWidth;
            ah = _activeHeight;
        }

        // No crop: unusable bounds, or the active region already covers the whole captured frame.
        if (aw <= 0 || ah <= 0 || (ax <= 0 && ay <= 0 && aw >= frameWidth && ah >= frameHeight))
        {
            x = 0; y = 0; w = 0; h = 0;
            return false;
        }

        // Clamp defensively against topology drift between capture and crop.
        x = Math.Clamp(ax, 0, Math.Max(0, frameWidth - 1));
        y = Math.Clamp(ay, 0, Math.Max(0, frameHeight - 1));
        w = Math.Clamp(aw, 1, frameWidth - x);
        h = Math.Clamp(ah, 1, frameHeight - y);
        return true;
    }

    private string BuildCropScaleFilter(int captureWidth, int captureHeight)
    {
        var cropWidth = _activeWidth;
        var cropHeight = _activeHeight;
        var cropX = _activeLeft - _screenLeft;
        var cropY = _activeTop - _screenTop;

        if (cropWidth == _screenWidth &&
            cropHeight == _screenHeight &&
            cropX == 0 &&
            cropY == 0)
        {
            return $"scale={captureWidth}:{captureHeight}";
        }

        return $"crop={cropWidth}:{cropHeight}:{cropX}:{cropY},scale={captureWidth}:{captureHeight}";
    }

    internal static IReadOnlyList<DesktopDisplayInfo> ParseXrandrDisplays(string[] lines)
    {
        var displays = new List<DesktopDisplayInfo>();
        foreach (var line in lines)
        {
            if (!line.Contains(" connected", StringComparison.Ordinal))
                continue;

            if (!TryParseXrandrGeometry(line, out var width, out var height, out var left, out var top))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;

            var outputName = parts[0];
            displays.Add(new DesktopDisplayInfo
            {
                DisplayId = outputName,
                PersistentDisplayKey = outputName,
                Name = outputName,
                IsPrimary = line.Contains(" primary ", StringComparison.Ordinal),
                Left = left,
                Top = top,
                Width = width,
                Height = height,
            });
        }

        return displays;
    }

    private static int ComputeDisplayListVersion(IEnumerable<DesktopDisplayInfo> displays)
    {
        var hash = new HashCode();
        foreach (var display in displays.OrderBy(item => item.DisplayId, StringComparer.Ordinal))
        {
            hash.Add(display.DisplayId, StringComparer.Ordinal);
            hash.Add(display.Left);
            hash.Add(display.Top);
            hash.Add(display.Width);
            hash.Add(display.Height);
            hash.Add(display.IsPrimary);
        }

        return Math.Abs(hash.ToHashCode()) + 1;
    }

    private async Task<int> CaptureWaylandAsync(string tool, string tmpFile, int quality,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        var toolName = Path.GetFileName(tool);

        if (toolName == "spectacle")
            return await CaptureWithSpectacleAsync(tool, tmpFile, quality, captureWidth, captureHeight, ct);

        if (toolName == "grim")
        {
            // grim outputs PNG; use ffmpeg to convert to JPEG with quality/scale
            var pngFile = tmpFile + ".png";
            try
            {
                var grimResult = await RunProcessAsync(tool, $"\"{pngFile}\"", ct);
                if (grimResult != 0 || !File.Exists(pngFile))
                    return grimResult;

                // Convert PNG to JPEG with quality and scale
                var ffmpegArgs = $"-i \"{pngFile}\" -vf \"{BuildCropScaleFilter(captureWidth, captureHeight)}\" -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{tmpFile}\"";
                return await RunProcessAsync("ffmpeg", ffmpegArgs, ct);
            }
            finally
            {
                try { if (File.Exists(pngFile)) File.Delete(pngFile); } catch { /* best effort */ }
            }
        }

        // Generic Wayland screenshot tool fallback
        return await RunProcessAsync(tool, $"\"{tmpFile}\"", ct);
    }

    private async Task<int> CaptureWithSpectacleAsync(string tool, string tmpFile, int quality,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        // spectacle -b (background) -n (no notification) -o (output file)
        // Capture to PNG first, then convert with ffmpeg for quality/scale control.
        var pngFile = tmpFile + ".png";
        try
        {
            var env = new Dictionary<string, string>();
            foreach (var key in new[] { "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "XDG_CURRENT_DESKTOP", "KDE_FULL_SESSION" })
            {
                var val = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(val)) env[key] = val;
            }

            // --cursor/-p includes the OS cursor in the capture (KDE Plasma 6 supports this).
            var result = await RunProcessAsync(tool, $"-b -n -p -o \"{pngFile}\"", ct, env);
            if (result != 0 || !File.Exists(pngFile))
            {
                _logger.LogWarning("spectacle capture failed (exit={Code}). Is spectacle installed?", result);
                return result;
            }

            // Crop to primary monitor first, then scale. If no primary detected, scale the full capture.
            var ffmpegArgs = $"-i \"{pngFile}\" -vf \"{BuildCropScaleFilter(captureWidth, captureHeight)}\" -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{tmpFile}\"";
            return await RunProcessAsync("ffmpeg", ffmpegArgs, ct);
        }
        finally
        {
            try { if (File.Exists(pngFile)) File.Delete(pngFile); } catch { /* best effort */ }
        }
    }

    private async Task<int> CaptureX11Async(string tool, string tmpFile, int quality, double scale,
        int captureWidth, int captureHeight, CancellationToken ct)
    {
        var toolName = Path.GetFileName(tool);
        if (toolName == "scrot")
        {
            var env = new Dictionary<string, string> { ["DISPLAY"] = _display };
            // -q sets JPEG quality. -z would suppress the cursor, so we omit it
            // to include the OS cursor in the captured frame.
            var args = $"-q {quality} \"{tmpFile}\"";
            var result = await RunProcessAsync(tool, args, ct, env);
            if (result != 0) return result;

            // Post-process to crop and/or scale to the active target.
            var scaledFile = tmpFile + ".scaled.jpg";
            try
            {
                var ffmpegArgs = $"-i \"{tmpFile}\" -vf \"{BuildCropScaleFilter(captureWidth, captureHeight)}\" -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{scaledFile}\"";
                var scaleResult = await RunProcessAsync("ffmpeg", ffmpegArgs, ct, env);
                if (scaleResult == 0 && File.Exists(scaledFile))
                {
                    File.Move(scaledFile, tmpFile, overwrite: true);
                }
                return scaleResult;
            }
            finally
            {
                try { if (File.Exists(scaledFile)) File.Delete(scaledFile); } catch { /* best effort */ }
            }
        }

        // import (ImageMagick) fallback
        if (toolName == "import")
        {
            var env = new Dictionary<string, string> { ["DISPLAY"] = _display };
            var args = $"-window root -quality {quality} \"{tmpFile}\"";
            var result = await RunProcessAsync(tool, args, ct, env);
            if (result != 0)
            {
                return result;
            }

            var scaledFile = tmpFile + ".scaled.jpg";
            try
            {
                var ffmpegArgs = $"-i \"{tmpFile}\" -vf \"{BuildCropScaleFilter(captureWidth, captureHeight)}\" -q:v {Math.Max(1, 31 - quality * 31 / 100)} -y \"{scaledFile}\"";
                var scaleResult = await RunProcessAsync("ffmpeg", ffmpegArgs, ct, env);
                if (scaleResult == 0 && File.Exists(scaledFile))
                {
                    File.Move(scaledFile, tmpFile, overwrite: true);
                }

                return scaleResult;
            }
            finally
            {
                try { if (File.Exists(scaledFile)) File.Delete(scaledFile); } catch { /* best effort */ }
            }
        }

        return -1;
    }

    private async Task<int> CaptureWithFfmpegAsync(string tmpFile, int captureWidth, int captureHeight,
        int quality, CancellationToken ct)
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        var env = new Dictionary<string, string> { ["DISPLAY"] = display };
        var args = $"-f x11grab -video_size {_screenWidth}x{_screenHeight} -i {display}+{_screenLeft},{_screenTop} " +
                   $"-frames:v 1 -q:v {Math.Max(1, 31 - quality * 31 / 100)} " +
                   $"-vf \"{BuildCropScaleFilter(captureWidth, captureHeight)}\" -y \"{tmpFile}\"";
        return await RunProcessAsync("ffmpeg", args, ct, env);
    }

    private static DisplayServer DetectDisplayServer()
    {
        // Check WAYLAND_DISPLAY first (if set, the session is Wayland)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return DisplayServer.Wayland;

        // XDG_SESSION_TYPE is set by most modern display managers
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            return DisplayServer.Wayland;
        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase))
            return DisplayServer.X11;

        // Fallback: if DISPLAY is set, assume X11
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return DisplayServer.X11;

        return DisplayServer.Unknown;
    }

    private (string? primary, string? fallback) DetectCaptureTools()
    {
        string? primary = null;
        string? fallback = null;

        if (_displayServer == DisplayServer.Wayland)
        {
            // On KDE Plasma, grim requires zwlr_screencopy_manager_v1 which KDE does not implement.
            // Prefer spectacle on KDE; fall back to grim for wlroots compositors (Hyprland, Sway).
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            var isKde = desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);

            if (isKde)
                primary = FindExecutable("spectacle") ?? FindExecutable("grim");
            else
                primary = FindExecutable("grim") ?? FindExecutable("spectacle");

            fallback = FindExecutable("ffmpeg");
        }
        else
        {
            primary = FindExecutable("scrot") ?? FindExecutable("import");
            fallback = FindExecutable("ffmpeg");
        }

        if (primary is null && fallback is null)
        {
            _logger.LogWarning(
                "No screen capture tools found. Install spectacle or grim (Wayland), scrot (X11), or ffmpeg as a fallback.");
        }

        return (primary, fallback);
    }

    private void DetectScreenSize()
    {
        _detectedDisplays = [];

        // KDE Wayland (KWin) first: KWin is not a wlroots compositor, so wlr-randr fails, and
        // XWayland's xrandr view of KWin outputs is unreliable for per-monitor geometry. kscreen-doctor
        // talks to KScreen directly (same API as KDE's own display settings) and reports true per-output
        // geometry + the priority-1 primary — the reliable source for the per-monitor display selector.
        if (_displayServer == DisplayServer.Wayland && TryDetectWithKScreenDoctor()) return;

        // Try xrandr next (works on both X11 and XWayland)
        if (TryDetectWithXrandr()) return;

        // Try xdpyinfo (X11 only)
        if (_displayServer != DisplayServer.Wayland && TryDetectWithXdpyinfo()) return;

        // Try wlr-randr (Wayland with wlroots compositors)
        if (_displayServer == DisplayServer.Wayland && TryDetectWithWlrRandr()) return;

        SetDefaultSize();
    }

    // Matches a kscreen-doctor "Geometry: X,Y WxH" line (ANSI colour codes stripped first).
    /// <summary>
    /// Reads a number out of kscreen-doctor / xrandr / xdpyinfo output, invariantly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THESE TOOLS SPEAK ASCII AND THE HOST MIGHT NOT (RemEx-tiih). <c>int.Parse</c> and
    /// <c>int.TryParse</c> without a provider use <c>CurrentCulture</c>, and 57 runtime cultures —
    /// the ar, ckb, fa, he, ks, lrc, mzn, pa, ps, sd, ur and uz families — reject the ASCII sign
    /// these tools emit, because their <c>NegativeSign</c> and <c>PositiveSign</c> carry a directional
    /// mark such as U+061C or U+200E in front of it.
    /// </para>
    /// <para>
    /// THE BLAST RADIUS IS WIDER THAN "NEGATIVE COORDINATES", which is how this was filed.
    /// <see cref="XrandrGeometryRegex"/> matches <c>(?&lt;x&gt;[+-]\d+)</c> — a sign is REQUIRED, so
    /// even a primary monitor at the origin arrives as <c>+0</c>. Measured: <c>"+0"</c>, <c>"+1920"</c>
    /// and <c>"-1920"</c> are rejected by the same 57 cultures, while an unsigned <c>"1920"</c> is
    /// rejected by none. On an affected host the xrandr path therefore failed to parse the geometry of
    /// EVERY output, not merely of monitors left of or above the primary.
    /// </para>
    /// <para>
    /// What that costs is not a cosmetic topology error. <c>_screenLeft</c>/<c>_screenTop</c> are the
    /// minimum over outputs, and RemEx-dyvd made that origin load-bearing: <c>MoveMouse</c> subtracts
    /// it before handing coordinates to ydotool, whose <c>--absolute</c> is emulated as home-then-move
    /// and wants an offset rather than a position. A wrong origin aims the pointer at the wrong place,
    /// silently.
    /// </para>
    /// <para>
    /// Resolutions, dimensions and priorities cannot be negative and go through this anyway, for the
    /// reason the formatting side does (RemEx-hbma): one rule with no exceptions is what keeps the
    /// signed cases safe by construction rather than by anyone remembering which values carry a sign.
    /// </para>
    /// </remarks>
    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Throwing form of <see cref="TryParseInt"/>, for the kscreen path whose caller already sits
    /// inside a try/catch and treats a malformed line as a parse failure.
    /// </summary>
    private static int ParseInt(string text) =>
        int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static readonly Regex KScreenGeometryRegex = new(
        @"Geometry:\s*(?<x>-?\d+),(?<y>-?\d+)\s+(?<w>\d+)x(?<h>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    /// <summary>
    /// Enumerates per-monitor geometry on KDE Plasma (Wayland or X11) via <c>kscreen-doctor -o</c>.
    /// Each enabled+connected output becomes a <see cref="DesktopDisplayInfo"/>; the priority-1 output
    /// is the primary. Populates the virtual-desktop bounding box and <c>_detectedDisplays</c> so the
    /// display catalog can offer per-monitor capture. Returns false (falling through to xrandr) when
    /// kscreen-doctor is absent or yields no usable output.
    /// </summary>
    private bool TryDetectWithKScreenDoctor()
    {
        try
        {
            var psi = new ProcessStartInfo("kscreen-doctor", "-o")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            if (string.IsNullOrWhiteSpace(output)) return false;

            var clean = AnsiEscapeRegex.Replace(output, string.Empty);
            var displays = ParseKScreenDisplays(clean);
            if (displays.Count == 0) return false;

            // Virtual-desktop bounding box across all outputs.
            int minX = displays.Min(d => d.Left);
            int minY = displays.Min(d => d.Top);
            int maxRight = displays.Max(d => d.Left + d.Width);
            int maxBottom = displays.Max(d => d.Top + d.Height);

            _screenLeft = minX;
            _screenTop = minY;
            _screenWidth = maxRight - minX;
            _screenHeight = maxBottom - minY;
            _detectedDisplays = displays;

            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
            _primaryX = primary.Left;
            _primaryY = primary.Top;
            _primaryWidth = primary.Width;
            _primaryHeight = primary.Height;

            if (_activeWidth == 0 || _activeHeight == 0)
            {
                SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
            }

            _logger.LogInformation(
                "kscreen-doctor detected {Count} display(s); virtual desktop {W}x{H} at ({X},{Y}); primary={Primary}.",
                displays.Count, _screenWidth, _screenHeight, _screenLeft, _screenTop, primary.DisplayId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "kscreen-doctor display detection failed.");
            return false;
        }
    }

    // Parses the ANSI-stripped `kscreen-doctor -o` output into one DesktopDisplayInfo per
    // enabled+connected output. Block form:
    //   Output: <id> <NAME> <uuid>
    //       enabled
    //       connected
    //       priority <n>        (priority 1 == primary)
    //       Geometry: X,Y WxH
    /// <summary>
    /// Turns <c>kscreen-doctor -o</c> output into displays.
    /// </summary>
    /// <remarks>
    /// Internal so the KDE path's parsing can be tested without kscreen-doctor. It is the only place
    /// the throwing <see cref="ParseInt"/> is used, and testing that helper alone would not have shown
    /// whether the geometry line reaches it at all (RemEx-tiih).
    /// </remarks>
    internal static List<DesktopDisplayInfo> ParseKScreenDisplays(string cleanOutput)
    {
        var result = new List<DesktopDisplayInfo>();
        var lines = cleanOutput.Split('\n');

        string? name = null, uuid = null;
        bool enabled = false, connected = false, isPrimary = false;
        int? geomX = null, geomY = null, geomW = null, geomH = null;

        void Flush()
        {
            if (name is not null && enabled && connected &&
                geomW is > 0 && geomH is > 0)
            {
                result.Add(new DesktopDisplayInfo
                {
                    DisplayId = name,
                    PersistentDisplayKey = string.IsNullOrEmpty(uuid) ? name : uuid,
                    Name = name,
                    IsPrimary = isPrimary,
                    Left = geomX ?? 0,
                    Top = geomY ?? 0,
                    Width = geomW.Value,
                    Height = geomH.Value,
                });
            }
            name = uuid = null;
            enabled = connected = isPrimary = false;
            geomX = geomY = geomW = geomH = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("Output:", StringComparison.Ordinal))
            {
                Flush();
                var parts = line["Output:".Length..].Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // parts[0] = index, parts[1] = NAME, parts[2] = uuid (optional)
                if (parts.Length >= 2) name = parts[1];
                if (parts.Length >= 3) uuid = parts[2];
                continue;
            }

            if (line.Equals("enabled", StringComparison.OrdinalIgnoreCase)) enabled = true;
            else if (line.Equals("disabled", StringComparison.OrdinalIgnoreCase)) enabled = false;
            else if (line.Equals("connected", StringComparison.OrdinalIgnoreCase)) connected = true;
            else if (line.StartsWith("priority ", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseInt(line["priority ".Length..].Trim(), out var pr) && pr == 1)
                    isPrimary = true;
            }
            else
            {
                var m = KScreenGeometryRegex.Match(line);
                if (m.Success)
                {
                    geomX = ParseInt(m.Groups["x"].Value);
                    geomY = ParseInt(m.Groups["y"].Value);
                    geomW = ParseInt(m.Groups["w"].Value);
                    geomH = ParseInt(m.Groups["h"].Value);
                }
            }
        }
        Flush();

        // If kscreen-doctor reported no explicit priority-1 primary, treat the first as primary.
        if (result.Count > 0 && !result.Any(d => d.IsPrimary))
        {
            result[0] = result[0] with { IsPrimary = true };
        }
        return result;
    }

    private bool TryDetectWithXrandr()
    {
        try
        {
            var env = new Dictionary<string, string>();
            var displayVar = Environment.GetEnvironmentVariable("DISPLAY");
            if (!string.IsNullOrEmpty(displayVar))
                env["DISPLAY"] = displayVar;

            var psi = new ProcessStartInfo("xrandr", "--current")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            var lines = SplitLines(output);
            var displays = ParseXrandrDisplays(lines).ToList();
            if (TryGetVirtualDesktopBounds(lines, out var width, out var height, out var left, out var top))
            {
                _screenLeft = left;
                _screenTop = top;
                _screenWidth = width;
                _screenHeight = height;
                _detectedDisplays = displays;

                var primaryDisplay = displays.FirstOrDefault(display => display.IsPrimary) ?? displays.FirstOrDefault();
                if (primaryDisplay is not null)
                {
                    _primaryX = primaryDisplay.Left;
                    _primaryY = primaryDisplay.Top;
                    _primaryWidth = primaryDisplay.Width;
                    _primaryHeight = primaryDisplay.Height;
                }
                else
                {
                    _primaryX = left;
                    _primaryY = top;
                    _primaryWidth = width;
                    _primaryHeight = height;
                }

                if (_activeWidth == 0 || _activeHeight == 0)
                {
                    SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "xrandr screen-size detection failed.");
        }
        return false;
    }
    private bool TryDetectWithXdpyinfo()
    {
        try
        {
            var psi = new ProcessStartInfo("xdpyinfo")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var displayVar = Environment.GetEnvironmentVariable("DISPLAY");
            if (!string.IsNullOrEmpty(displayVar))
                psi.Environment["DISPLAY"] = displayVar;

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("dimensions:"))
                {
                    var parts = line.Split(':')[1].Trim().Split(' ')[0].Split('x');
                    if (parts.Length == 2 && TryParseInt(parts[0], out int w) && TryParseInt(parts[1], out int h))
                    {
                        _screenLeft = 0;
                        _screenTop = 0;
                        _screenWidth = w;
                        _screenHeight = h;
                        _detectedDisplays =
                        [CreateFallbackDisplay(0, 0, w, h)];
                        SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
                        return true;
                    }
                }
            }
        }
        catch { /* fall through */ }
        return false;
    }

    private bool TryDetectWithWlrRandr()
    {
        try
        {
            var psi = new ProcessStartInfo("wlr-randr")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Parse "current: 1920x1080"
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("current:"))
                {
                    var resPart = trimmed.Split("current:")[1].Trim().Split(' ')[0];
                    var res = resPart.Split('x');
                    if (res.Length == 2 && TryParseInt(res[0], out int w) && TryParseInt(res[1], out int h))
                    {
                        _screenLeft = 0;
                        _screenTop = 0;
                        _screenWidth = w;
                        _screenHeight = h;
                        _detectedDisplays =
                        [CreateFallbackDisplay(0, 0, w, h)];
                        SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
                        return true;
                    }
                }
            }
        }
        catch { /* fall through */ }
        return false;
    }

    private void SetDefaultSize()
    {
        _screenLeft = 0;
        _screenTop = 0;
        _screenWidth = 1920;
        _screenHeight = 1080;
        _primaryWidth = 0; // 0 = no primary detected, use full capture
        _detectedDisplays = [CreateFallbackDisplay()];
        SetActiveBounds(_screenWidth, _screenHeight, _screenLeft, _screenTop);
        _logger.LogWarning("Could not detect screen size, defaulting to {W}x{H}.", _screenWidth, _screenHeight);
    }

    private static string? FindExecutable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(path) ? path : null;
        }
        catch { return null; }
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments,
        CancellationToken ct, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (env is not null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return -1;

            var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(proc.WaitForExitAsync(ct), stdOutTask, stdErrTask);
            return proc.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string[] SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static bool TryGetVirtualDesktopBounds(
        string[] lines,
        out int width,
        out int height,
        out int left,
        out int top)
    {
        width = 0;
        height = 0;
        left = 0;
        top = 0;

        int minX = 0;
        int minY = 0;
        bool foundOffset = false;

        foreach (var line in lines)
        {
            if (!line.StartsWith("Screen ", StringComparison.Ordinal) || !line.Contains("current ", StringComparison.Ordinal))
                continue;

            var currentIndex = line.IndexOf("current ", StringComparison.Ordinal);
            if (currentIndex < 0)
                continue;

            var dimsSection = line[(currentIndex + "current ".Length)..];
            var commaIndex = dimsSection.IndexOf(',');
            if (commaIndex >= 0)
                dimsSection = dimsSection[..commaIndex];

            var dims = dimsSection.Split(" x ", StringSplitOptions.TrimEntries);
            if (dims.Length != 2 ||
                !TryParseInt(dims[0], out width) ||
                !TryParseInt(dims[1], out height))
            {
                continue;
            }

            foreach (var innerLine in lines)
            {
                if (!TryParseXrandrGeometry(innerLine, out _, out _, out var x, out var y))
                    continue;

                minX = foundOffset ? Math.Min(minX, x) : x;
                minY = foundOffset ? Math.Min(minY, y) : y;
                foundOffset = true;
            }

            left = foundOffset ? minX : 0;
            top = foundOffset ? minY : 0;
            return true;
        }

        int maxX = 0;
        int maxY = 0;
        bool foundAny = false;

        foreach (var line in lines)
        {
            if (!TryParseXrandrGeometry(line, out var currentWidth, out var currentHeight, out var x, out var y))
                continue;

            if (!foundAny)
            {
                minX = x;
                minY = y;
                maxX = x + currentWidth;
                maxY = y + currentHeight;
                foundAny = true;
            }
            else
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x + currentWidth);
                maxY = Math.Max(maxY, y + currentHeight);
            }
        }

        if (!foundAny)
            return false;

        left = minX;
        top = minY;
        width = maxX - minX;
        height = maxY - minY;
        return true;
    }

    /// <summary>
    /// Parses one xrandr geometry token such as <c>1920x1080+0+0</c> or <c>1920x1080-1920+0</c>.
    /// </summary>
    /// <remarks>
    /// Internal so the regex and the parse can be tested together, which is where the defect lived:
    /// the pattern REQUIRES a sign on x and y, so testing the helper alone would miss that every
    /// output, not merely a left-of-primary one, went through a signed parse (RemEx-tiih).
    /// </remarks>
    internal static bool TryParseXrandrGeometry(
        string line,
        out int width,
        out int height,
        out int x,
        out int y)
    {
        width = 0;
        height = 0;
        x = 0;
        y = 0;

        if (!line.Contains(" connected", StringComparison.Ordinal) || !line.Contains('x'))
            return false;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!part.Contains('x'))
                continue;

            var match = XrandrGeometryRegex.Match(part);
            if (!match.Success)
                continue;

            if (!TryParseInt(match.Groups["width"].Value, out width) ||
                !TryParseInt(match.Groups["height"].Value, out height) ||
                !TryParseInt(match.Groups["x"].Value, out x) ||
                !TryParseInt(match.Groups["y"].Value, out y))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
