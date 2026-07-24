using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Services; // CaptureScaling (relocated to Remex.Core so the WGC project can share it)

namespace Remex.Agent.Services.ScreenCapture;

/// <summary>
/// DXGI Desktop Duplication API based screen capture for Windows 10/11.
///
/// Correctly captures GPU-composited content — including hardware overlay planes (MPO)
/// used by Windows Terminal, Chrome GPU compositing, and other DirectX-accelerated apps —
/// which GDI BitBlt/CopyFromScreen cannot capture.
///
/// Falls back gracefully: returns null so the caller can use GDI instead.
/// Thread-safe: concurrent callers get the last captured frame immediately.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DxgiDesktopCapture : IDisposable
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IntPtr _d3dDevice = IntPtr.Zero;
    private IntPtr _d3dContext = IntPtr.Zero;
    private IntPtr _duplOutput = IntPtr.Zero;
    private IntPtr _stagingTexture = IntPtr.Zero;
    private byte[]? _lastFrame;
    private CursorShapeSnapshot? _lastPointerShape;

    private bool _disposed;
    private bool _initialized;

    // Rate-limits DuplicateOutput re-initialization after the duplication is lost. A display powering
    // off (idle sleep), a fullscreen app flipping, or a secure desktop (UAC/lock) makes AcquireNextFrame
    // return ACCESS_LOST — often on EVERY frame for the whole transition. Re-creating the duplication on
    // each loss would call DuplicateOutput at the stream's full frame rate against a display mid
    // power-transition, which wedges DWM (dwmredir.dll) + the NVIDIA driver and black-screens the host
    // (RemEx-crk). The throttle caps re-init to one attempt per (escalating) backoff window; a healthy
    // frame resets it so a one-off loss (UAC/resolution change) still recovers within the base interval.
    private readonly DuplicationReinitThrottle _reinitThrottle =
        new(baseBackoff: TimeSpan.FromSeconds(1), maxBackoff: TimeSpan.FromSeconds(8));

    public int Width  { get; private set; }
    public int Height { get; private set; }
    public int DesktopLeft { get; private set; }
    public int DesktopTop  { get; private set; }
    public string? OutputDeviceName { get; private set; }
    public bool IsAvailable => _duplOutput != IntPtr.Zero && !_disposed;
    public string? UnavailableReason { get; private set; }

    // ── HRESULT constants ─────────────────────────────────────────────────────
    private const int S_OK                              = 0;
    private const int DXGI_ERROR_WAIT_TIMEOUT           = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST            = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_SESSION_DISCONNECTED   = unchecked((int)0x887A0028);
    private const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);

    // ── D3D11 / DXGI constants ────────────────────────────────────────────────
    private const int  D3D_DRIVER_TYPE_HARDWARE   = 1;
    private const uint D3D11_SDK_VERSION          = 7;
    private const int  DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int  D3D11_USAGE_STAGING        = 3;
    private const uint D3D11_CPU_ACCESS_READ      = 0x20000;
    private const int  D3D11_MAP_READ             = 1;

    // ── Interface GUIDs for QueryInterface ───────────────────────────────────
    private static readonly Guid IID_IDXGIDevice    = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDXGIOutput1   = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, int FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    // ── COM delegate types (for vtable dispatch without unsafe code) ──────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  QueryInterfaceFn(IntPtr self, ref Guid riid, out IntPtr ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetImmediateContextFn(IntPtr self, out IntPtr ppContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  CreateTexture2DFn(IntPtr self, IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  GetAdapterFn(IntPtr self, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  EnumOutputsFn(IntPtr self, uint output, out IntPtr ppOutput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  DuplicateOutputFn(IntPtr self, IntPtr pDevice, out IntPtr ppDuplication);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDuplDescFn(IntPtr self, IntPtr pDesc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  AcquireNextFrameFn(IntPtr self, uint timeoutMs, IntPtr pFrameInfo, out IntPtr ppResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  ReleaseFrameFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFramePointerShapeFn(
        IntPtr self,
        uint bufferSize,
        IntPtr pointerShapeBuffer,
        out uint requiredBufferSize,
        out DXGI_OUTDUPL_POINTER_SHAPE_INFO pointerShapeInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  GetDescFn(IntPtr self, IntPtr pDesc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceFn(IntPtr self, IntPtr pDst, IntPtr pSrc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  MapFn(IntPtr self, IntPtr pResource, uint Subresource, int MapType, uint MapFlags, IntPtr pMappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapFn(IntPtr self, IntPtr pResource, uint Subresource);

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public RECT DesktopCoordinates;
        public bool AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int  Format;
        public uint SampleDescCount, SampleDescQuality;
        public int  Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    // DXGI_OUTDUPL_DESC — used to read Width/Height of the duplicated output
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_DESC
    {
        public uint ModeWidth, ModeHeight;       // DXGI_MODE_DESC.Width, Height
        public uint RefreshNum, RefreshDen;
        public int  ModeFormat, ScanlineOrdering, Scaling;
        public int  Rotation;
        public int  DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_POINTER_POSITION
    {
        public POINT Position;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        [MarshalAs(UnmanagedType.Bool)] public bool RectsCoalesced;
        [MarshalAs(UnmanagedType.Bool)] public bool ProtectedContentMaskedOut;
        public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_POINTER_SHAPE_INFO
    {
        public uint Type;
        public uint Width;
        public uint Height;
        public uint Pitch;
        public POINT HotSpot;
    }

    // D3D11_MAPPED_SUBRESOURCE
    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public IntPtr pData;
        public uint   RowPitch;
        public uint   DepthPitch;
    }

    // ── vtable dispatch helpers (no unsafe code) ──────────────────────────────

    private static T GetSlot<T>(IntPtr com, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(com);
        IntPtr fn     = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static int QueryInterface(IntPtr com, Guid iid, out IntPtr result)
    {
        var fn = GetSlot<QueryInterfaceFn>(com, 0); // IUnknown::QueryInterface = slot 0
        return fn(com, ref iid, out result);
    }

    private static void Release(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        GetSlot<ReleaseFn>(com, 2)(com); // IUnknown::Release = slot 2
        com = IntPtr.Zero;
    }

    // ── Construction / initialization ─────────────────────────────────────────

    public DxgiDesktopCapture(ILogger logger)
    {
        _logger = Guard.NotNull(logger);
        // Deferred: DXGI device/duplication not opened until the first actual capture.
        // The idle service must not hold a Desktop Duplication consumer slot with no
        // active client — that contends with Windows Remote Desktop's own consumer
        // ("something watching") and wedges DWM (RemEx-hmj).
    }

    // Must be called under _lock. Sets _initialized = true after the first attempt so
    // subsequent callers are no-ops; success or failure is reflected in IsAvailable / UnavailableReason.
    private void EnsureInitialized()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        try
        {
            InitializeDevice();
            InitializeDuplication();
            UnavailableReason = null;
            _logger.LogInformation("DXGI Desktop Duplication initialized ({W}×{H}).", Width, Height);
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
            _logger.LogInformation(
                "DXGI Desktop Duplication unavailable ({Msg}). GDI capture will be used.", ex.Message);
            ReleaseAll();
        }
    }

    private void InitializeDevice()
    {
        int hr = D3D11CreateDevice(
            IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
            IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out _d3dDevice, out _, out _d3dContext);

        if (hr != S_OK)
            throw new InvalidOperationException($"D3D11CreateDevice hr=0x{hr:X8}");
    }

    private void InitializeDuplication()
    {
        if (_d3dDevice == IntPtr.Zero)
            throw new InvalidOperationException("D3D device not initialized.");

        // device → IDXGIDevice
        int hr = QueryInterface(_d3dDevice, IID_IDXGIDevice, out var dxgiDevice);
        if (hr != S_OK) throw new InvalidOperationException($"QI IDXGIDevice hr=0x{hr:X8}");

        IntPtr adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero;
        try
        {
            // IDXGIDevice::GetAdapter is slot 7 (after 3 IUnknown + 4 IDXGIObject)
            hr = GetSlot<GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out adapter);
            if (hr != S_OK) throw new InvalidOperationException($"GetAdapter hr=0x{hr:X8}");

            // IDXGIAdapter::EnumOutputs(0) = primary output, slot 7
            hr = GetSlot<EnumOutputsFn>(adapter, 7)(adapter, 0, out output);
            if (hr != S_OK) throw new InvalidOperationException($"EnumOutputs hr=0x{hr:X8}");

            // IDXGIOutput → IDXGIOutput1
            hr = QueryInterface(output, IID_IDXGIOutput1, out output1);
            if (hr != S_OK) throw new InvalidOperationException($"QI IDXGIOutput1 hr=0x{hr:X8}");

            // Read output position from IDXGIOutput::GetDesc (slot 7)
            // Slot layout: IUnknown (0-2) + IDXGIObject (3-6) → IDXGIOutput::GetDesc = 7
            IntPtr outputDescPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_OUTPUT_DESC>());
            try
            {
                GetSlot<GetDescFn>(output, 7)(output, outputDescPtr);
                var desc = Marshal.PtrToStructure<DXGI_OUTPUT_DESC>(outputDescPtr);
                DesktopLeft = desc.DesktopCoordinates.Left;
                DesktopTop  = desc.DesktopCoordinates.Top;
                OutputDeviceName = desc.DeviceName;
                _logger.LogDebug("Captured monitor coordinates: ({L}, {T})", DesktopLeft, DesktopTop);
            }
            finally { Marshal.FreeHGlobal(outputDescPtr); }

            // IDXGIOutput1::DuplicateOutput = slot 22
            hr = GetSlot<DuplicateOutputFn>(output1, 22)(output1, _d3dDevice, out _duplOutput);
            if (hr == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)
                throw new InvalidOperationException("DXGI_ERROR_NOT_CURRENTLY_AVAILABLE — max consumers reached or running over RDP.");
            if (hr != S_OK) throw new InvalidOperationException($"DuplicateOutput hr=0x{hr:X8}");

            // Read output dimensions from IDXGIOutputDuplication::GetDesc (slot 7)
            IntPtr descPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_OUTDUPL_DESC>());
            try
            {
                GetSlot<GetDuplDescFn>(_duplOutput, 7)(_duplOutput, descPtr);
                var desc = Marshal.PtrToStructure<DXGI_OUTDUPL_DESC>(descPtr);
                Width  = (int)desc.ModeWidth;
                Height = (int)desc.ModeHeight;
            }
            finally { Marshal.FreeHGlobal(descPtr); }

            if (Width <= 0 || Height <= 0)
                throw new InvalidOperationException($"DXGI reported zero-size output ({Width}×{Height}).");

            // Create CPU-readable staging texture (same dimensions + format as the desktop surface)
            var texDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)Width, Height = (uint)Height,
                MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDescCount = 1, SampleDescQuality = 0,
                Usage = D3D11_USAGE_STAGING,
                BindFlags = 0, CPUAccessFlags = D3D11_CPU_ACCESS_READ, MiscFlags = 0
            };

            IntPtr texDescPtr = Marshal.AllocHGlobal(Marshal.SizeOf<D3D11_TEXTURE2D_DESC>());
            try
            {
                Marshal.StructureToPtr(texDesc, texDescPtr, false);
                // ID3D11Device::CreateTexture2D = slot 5 (after 3 IUnknown)
                hr = GetSlot<CreateTexture2DFn>(_d3dDevice, 5)(_d3dDevice, texDescPtr, IntPtr.Zero, out _stagingTexture);
            }
            finally { Marshal.FreeHGlobal(texDescPtr); }

            if (hr != S_OK) throw new InvalidOperationException($"CreateTexture2D hr=0x{hr:X8}");
            _stagingHasFrame = false; // fresh staging texture — no frame copied into it yet
        }
        finally
        {
            Release(ref output1);
            Release(ref output);
            Release(ref adapter);
            Release(ref dxgiDevice);
        }
    }

    // ── Capture ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the current desktop frame using DXGI Desktop Duplication.
    /// Returns null if DXGI is unavailable (caller should fall back to GDI).
    /// Returns the last captured JPEG if the desktop hasn't changed since the last capture
    /// (DXGI_ERROR_WAIT_TIMEOUT), reducing CPU/bandwidth on static screens.
    /// Thread-safe: concurrent callers get the last frame without blocking.
    /// </summary>
    public byte[]? TryCapture(int quality, double scale, ImageCodecInfo jpegEncoder, bool drawCursor, out bool isLive)
    {
        isLive = false;
        if (_disposed) return null;

        // Non-blocking: if another capture is already in progress, return last frame immediately.
        // This prevents queue buildup for high-FPS streams with multiple concurrent connections.
        // A concurrent in-flight capture is doing the real work, so this replay counts as live.
        if (!_lock.Wait(0))
        {
            isLive = true;
            return _lastFrame;
        }

        try
        {
            EnsureInitialized();
            if (!IsAvailable) return null; // caller falls back to GDI; isLive is unused on a null return
            return CaptureInternal(quality, scale, jpegEncoder, drawCursor, out isLive);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("DXGI capture error: {Msg}", ex.Message);
            return _lastFrame; // stale: a thrown capture is a genuine failure
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[]? _lastRawFrame;
    // Scale that produced _lastRawFrame (-1 = none yet). A cached frame is a valid replay ONLY for
    // a request at the same scale: replaying it across a preset change hands the encoder a
    // wrong-size buffer, which desyncs its fixed -s WxH rawvideo input and forces an encoder
    // reinit every frame — and on a static desktop (no new duplication frame to re-derive from)
    // that starves the stream indefinitely (RemEx-wmm8).
    private double _lastRawFrameScale = -1;
    // Whether _stagingTexture still holds the most recent frame's pixels, letting a scale change
    // on a static desktop re-derive a correctly-sized output instead of starving.
    private bool _stagingHasFrame;

    // isLive distinguishes a fresh/confirmed-healthy capture from a STALE cached replay (RemEx-ltd). It is
    // true exactly where the duplication is confirmed alive (a real frame, or a no-change frame on a healthy
    // output — the same points RecordHealthyFrame fires); false when a cached frame is replayed because the
    // output was lost/errored, so a sustained freeze stops counting as a successful capture upstream.
    public byte[]? TryCaptureRaw(double scale, bool drawCursor, out bool isLive)
    {
        isLive = false;
        if (_disposed) return null;

        // Another capture holds the lock: returning its in-flight cached frame is not a stall (the concurrent
        // capture is doing the real work), so report live — but only when the cache matches the requested
        // scale. A wrong-scale replay reported live would be fed to the encoder and desync its fixed-size
        // rawvideo input, so report it stale instead.
        if (!_lock.Wait(0))
        {
            var cached = _lastRawFrame;
            isLive = cached is not null && _lastRawFrameScale == scale;
            return cached;
        }

        try
        {
            EnsureInitialized();
            if (!IsAvailable) return null; // caller falls back to GDI; isLive is unused on a null return
            return CaptureRawInternal(scale, drawCursor, out isLive);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("DXGI raw capture error: {Msg}", ex.Message);
            return _lastRawFrame; // stale: a thrown capture is a genuine failure, not a static screen
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[]? CaptureRawInternal(double scale, bool drawCursor, out bool isLive)
    {
        isLive = false; // stale until a healthy path proves otherwise
        IntPtr dxgiResource = IntPtr.Zero;
        var hr = AcquireNextFrame(50u, out var frameInfo, out dxgiResource);

        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        {
            _reinitThrottle.RecordHealthyFrame(); // output alive, just no change — clears loss escalation
            // Healthy static screen: replay the cache only when it matches the requested scale;
            // otherwise re-derive from the retained staging pixels (RemEx-wmm8).
            return ReplayOrRescale(scale, drawCursor, out isLive);
        }

        if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_SESSION_DISCONNECTED)
        {
            _logger.LogInformation("DXGI access lost (hr=0x{Hr:X8}) — reinitializing.", hr);
            TryReinitializeDuplication();
            return _lastRawFrame; // stale: output lost
        }

        if (hr != S_OK)
        {
            _logger.LogDebug("AcquireNextFrame hr=0x{Hr:X8}", hr);
            return _lastRawFrame; // stale: acquire failed
        }

        _reinitThrottle.RecordHealthyFrame(); // acquired a real frame — output confirmed healthy

        try
        {
            UpdatePointerShapeFromFrame(frameInfo);

            if (frameInfo.AccumulatedFrames == 0 && (_lastRawFrame is not null || _stagingHasFrame))
            {
                // Healthy output, no new content this tick — same replay-or-rescale as WAIT_TIMEOUT.
                return ReplayOrRescale(scale, drawCursor, out isLive);
            }

            hr = QueryInterface(dxgiResource, IID_ID3D11Texture2D, out var srcTex);
            if (hr != S_OK) return _lastRawFrame; // stale: couldn't read the acquired frame

            try
            {
                GetSlot<CopyResourceFn>(_d3dContext, 47)(_d3dContext, _stagingTexture, srcTex);
                _stagingHasFrame = true;
            }
            finally
            {
                Release(ref srcTex);
            }
        }
        finally
        {
            Release(ref dxgiResource);
            GetSlot<ReleaseFrameFn>(_duplOutput, 14)(_duplOutput);
        }

        return MapStagingAndEncode(scale, drawCursor, out isLive);
    }

    // Healthy no-new-frame tick: the cached frame is a valid replay only at the scale that produced
    // it. After a scale change, re-derive the output from the retained staging pixels so the encoder
    // keeps receiving correctly-sized frames on a static desktop (RemEx-wmm8).
    private byte[]? ReplayOrRescale(double scale, bool drawCursor, out bool isLive)
    {
        if (_lastRawFrame is not null && _lastRawFrameScale == scale)
        {
            isLive = true;
            return _lastRawFrame;
        }

        if (_stagingHasFrame)
        {
            return MapStagingAndEncode(scale, drawCursor, out isLive);
        }

        isLive = true; // healthy, but nothing captured yet to re-derive from
        return _lastRawFrame;
    }

    // Must be called under _lock with a valid frame in _stagingTexture (_stagingHasFrame). Maps the
    // staging texture and converts it to the even-aligned target size for the requested scale,
    // refreshing the cached frame and its scale marker. Serves both the fresh-frame path and the
    // static-desktop rescale path.
    private byte[]? MapStagingAndEncode(double scale, bool drawCursor, out bool isLive)
    {
        IntPtr mappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MappedSubresource>());
        var hr = GetSlot<MapFn>(_d3dContext, 14)(
            _d3dContext, _stagingTexture, 0, D3D11_MAP_READ, 0, mappedPtr);

        if (hr != S_OK)
        {
            Marshal.FreeHGlobal(mappedPtr);
            isLive = false;
            return _lastRawFrame; // stale: map failed
        }

        try
        {
            var mapped = Marshal.PtrToStructure<MappedSubresource>(mappedPtr);
            _lastRawFrame = EncodeToRawBgra(mapped.pData, (int)mapped.RowPitch, Width, Height, scale, drawCursor, DesktopLeft, DesktopTop);
            _lastRawFrameScale = scale;
            isLive = true; // fresh frame produced
            return _lastRawFrame;
        }
        finally
        {
            GetSlot<UnmapFn>(_d3dContext, 15)(_d3dContext, _stagingTexture, 0);
            Marshal.FreeHGlobal(mappedPtr);
        }
    }

    private static byte[] EncodeToRawBgra(IntPtr pixelData, int rowPitch, int width, int height,
        double scale, bool drawCursor, int originX, int originY)
    {
        // RD-C fast path: no cursor compositing AND no resample → direct row-wise copy, skipping the GDI+
        // blit + intermediate Bitmaps that capped the stream near 30 FPS. See BgraFrameConverter /
        // docs/REMOTE_DESKTOP_PERFORMANCE.md. (With a separate cursor overlay, drawCursor is normally false.)
        if (!drawCursor)
        {
            var fast = BgraFrameConverter.TryConvertNoScale(pixelData, rowPitch, width, height, scale);
            if (fast is not null)
            {
                return fast;
            }
        }

        using var src = new Bitmap(width, height, rowPitch, PixelFormat.Format32bppArgb, pixelData);
        using var writable = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(writable))
        {
            g.DrawImage(src, 0, 0, width, height);
        }

        if (drawCursor)
        {
            DrawCursorOnBitmap(writable, originX, originY);
        }

        // Even-aligned target size — MUST match CaptureScaling.ScaledEven so the raw BGRA buffer is
        // exactly the size the H.264 encoder was started with (-s WxH). A 1-pixel mismatch desyncs
        // the rawvideo pipe and nvenc emits 0 frames.
        int sw = CaptureScaling.ScaledEven(width, scale);
        int sh = CaptureScaling.ScaledEven(height, scale);

        Bitmap output;
        if (sw != width || sh != height)
        {
            output = new Bitmap(sw, sh);
            using var g = Graphics.FromImage(output);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            // SourceCopy (not the default SourceOver): the GDI-drawn cursor pixels have alpha 0, and
            // alpha-blended scaling would drop them — making the cursor invisible while the desktop
            // (alpha 255) survives. SourceCopy copies RGB directly; alpha is irrelevant downstream
            // (BGRA→yuv420p / JPEG ignore it).
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(writable, 0, 0, sw, sh);
        }
        else
        {
            output = writable;
        }

        try
        {
            return GetRawBgraBytes(output);
        }
        finally
        {
            if (!ReferenceEquals(output, writable))
            {
                output.Dispose();
            }
        }
    }

    private static byte[] GetRawBgraBytes(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
            byte[] bgraValues = new byte[bytes];
            Marshal.Copy(bmpData.Scan0, bgraValues, 0, bytes);
            return bgraValues;
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
    }

    private byte[]? CaptureInternal(int quality, double scale, ImageCodecInfo jpegEncoder, bool drawCursor, out bool isLive)
    {
        isLive = false; // stale until a healthy path proves otherwise
        // AcquireNextFrame — slot 8 on IDXGIOutputDuplication
        // Timeout 50ms: wait up to 50ms for a new frame.
        // Returns DXGI_ERROR_WAIT_TIMEOUT if no new frame; we reuse last JPEG (desktop unchanged).
        IntPtr dxgiResource = IntPtr.Zero;
        var hr = AcquireNextFrame(50u, out var frameInfo, out dxgiResource);

        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        {
            _reinitThrottle.RecordHealthyFrame(); // output alive, just no change — clears loss escalation
            isLive = true; // healthy static screen
            return _lastFrame; // Desktop unchanged — bandwidth-efficient reuse
        }

        if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_SESSION_DISCONNECTED)
        {
            _logger.LogInformation("DXGI access lost (hr=0x{Hr:X8}) — reinitializing.", hr);
            TryReinitializeDuplication();
            return _lastFrame; // stale: output lost
        }

        if (hr != S_OK)
        {
            _logger.LogDebug("AcquireNextFrame hr=0x{Hr:X8}", hr);
            return _lastFrame; // stale: acquire failed
        }

        _reinitThrottle.RecordHealthyFrame(); // acquired a real frame — output confirmed healthy

        try
        {
            UpdatePointerShapeFromFrame(frameInfo);

            if (frameInfo.AccumulatedFrames == 0 && _lastFrame is not null)
            {
                isLive = true; // healthy output, no new content this tick
                return _lastFrame;
            }

            // QI IDXGIResource → ID3D11Texture2D
            hr = QueryInterface(dxgiResource, IID_ID3D11Texture2D, out var srcTex);
            if (hr != S_OK) return _lastFrame; // stale: couldn't read the acquired frame

            try
            {
                // Copy GPU-resident texture → CPU-readable staging texture
                // ID3D11DeviceContext::CopyResource = slot 47
                GetSlot<CopyResourceFn>(_d3dContext, 47)(_d3dContext, _stagingTexture, srcTex);
            }
            finally
            {
                Release(ref srcTex);
            }
        }
        finally
        {
            Release(ref dxgiResource);
            // Release the DXGI frame ASAP so the OS can recycle its buffer.
            // Must be released before calling AcquireNextFrame again.
            GetSlot<ReleaseFrameFn>(_duplOutput, 14)(_duplOutput);
        }

        // Map the staging texture for CPU read
        // ID3D11DeviceContext::Map = slot 14
        IntPtr mappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MappedSubresource>());
        hr = GetSlot<MapFn>(_d3dContext, 14)(
            _d3dContext, _stagingTexture, 0, D3D11_MAP_READ, 0, mappedPtr);

        if (hr != S_OK)
        {
            Marshal.FreeHGlobal(mappedPtr);
            return _lastFrame; // stale: map failed
        }

        try
        {
            var mapped = Marshal.PtrToStructure<MappedSubresource>(mappedPtr);
            _lastFrame = EncodeToJpeg(mapped.pData, (int)mapped.RowPitch, Width, Height, quality, scale, jpegEncoder, drawCursor, DesktopLeft, DesktopTop);
            isLive = true; // fresh frame produced
            return _lastFrame;
        }
        finally
        {
            // ID3D11DeviceContext::Unmap = slot 15
            GetSlot<UnmapFn>(_d3dContext, 15)(_d3dContext, _stagingTexture, 0);
            Marshal.FreeHGlobal(mappedPtr);
        }
    }

    private static byte[] EncodeToJpeg(IntPtr pixelData, int rowPitch, int width, int height,
        int quality, double scale, ImageCodecInfo jpegEncoder, bool drawCursor, int originX, int originY)
    {
        // Wrap the DXGI-mapped BGRA memory as a read-only Bitmap (D3D11_MAP_READ).
        // We must NOT draw into this bitmap — the staging texture is mapped read-only.
        using var src = new Bitmap(width, height, rowPitch, PixelFormat.Format32bppArgb, pixelData);

        // Copy to a writable bitmap so we can draw the cursor overlay safely.
        // This also decouples us from the mapped GPU memory lifetime.
        using var writable = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(writable))
        {
            g.DrawImage(src, 0, 0, width, height);
        }

        if (drawCursor)
        {
            DrawCursorOnBitmap(writable, originX, originY);
        }

        Bitmap output;
        if (scale < 1.0)
        {
            int sw = (int)(width * scale);
            int sh = (int)(height * scale);
            output = new Bitmap(sw, sh);
            using var g = Graphics.FromImage(output);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            // SourceCopy so the alpha-0 GDI cursor pixels survive scaling (see EncodeToRawBgra).
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(writable, 0, 0, sw, sh);
        }
        else
        {
            output = writable;
        }

        try
        {
            using var ms = new MemoryStream();
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            output.Save(ms, jpegEncoder, ep);
            return ms.ToArray();
        }
        finally
        {
            // Only dispose the scaled copy; writable is handled by its own using statement
            if (!ReferenceEquals(output, writable))
                output.Dispose();
        }
    }

    // ── Reinitialization on ACCESS_LOST ────────────────────────────────────────

    private void TryReinitializeDuplication()
    {
        // Release the lost duplication + staging texture FIRST so IsAvailable reports false (callers fall
        // back to GDI) and we never AcquireNextFrame against a dead output. Keep D3D device/context alive.
        Release(ref _stagingTexture);
        _stagingHasFrame = false;
        Release(ref _duplOutput);
        Width = 0;
        Height = 0;

        // Throttle the DuplicateOutput re-init. While a display is powering off or an app is flipping
        // fullscreen, AcquireNextFrame returns ACCESS_LOST every frame; without this gate the stream loop
        // — which treats the cached frame returned below as a successful capture and so never engages its
        // own failure-backoff — would re-create the duplication at the full frame rate and wedge the GPU
        // driver (RemEx-crk). Skip until the (escalating) backoff window elapses; TryRecover() retries later.
        var now = DateTime.UtcNow;
        if (!_reinitThrottle.ShouldAttempt(now))
            return;
        _reinitThrottle.RecordAttempt(now);

        try
        {
            InitializeDuplication();
            UnavailableReason = null;
            _logger.LogInformation("DXGI Desktop Duplication reinitialized ({W}×{H}).", Width, Height);
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
            // Normal while a secure desktop (UAC/lock) is up or the display is asleep; the escalating
            // backoff (already advanced by RecordAttempt) keeps retries from hammering the driver.
            _logger.LogWarning("DXGI reinitialize failed: {Msg}. Falling back to GDI; will retry shortly.", ex.Message);
        }
    }

    /// <summary>
    /// Attempts to bring DXGI duplication back online after it was lost (ACCESS_LOST /
    /// SESSION_DISCONNECTED that couldn't reinitialize in-line). Safe to call every frame:
    /// it no-ops when already available, when disposed, or until the backoff window elapses.
    /// Without this, a single transient loss (e.g. a UAC prompt) would strand capture on GDI
    /// for the remainder of the session.
    /// </summary>
    public void TryRecover()
    {
        if (IsAvailable || _disposed) return;
        // Pre-lock throttle check: skip only when _initialized (i.e. first-time init already ran).
        // Before first init, _initialized is false so we always fall through to EnsureInitialized().
        if (_initialized && !_reinitThrottle.ShouldAttempt(DateTime.UtcNow)) return;

        // Non-blocking: if a capture is mid-flight, skip this attempt.
        if (!_lock.Wait(0)) return;
        try
        {
            EnsureInitialized();
            if (IsAvailable || _disposed) return;
            var now = DateTime.UtcNow;
            if (!_reinitThrottle.ShouldAttempt(now)) return; // re-check under lock
            _reinitThrottle.RecordAttempt(now);

            // The D3D device usually survives a desktop switch; recreate it only if it was torn down
            // (e.g. driver reset / TDR) so duplication has a valid device to bind to.
            if (_d3dDevice == IntPtr.Zero)
                InitializeDevice();

            InitializeDuplication();
            UnavailableReason = null;
            _logger.LogInformation("DXGI Desktop Duplication recovered ({W}×{H}).", Width, Height);
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
            _logger.LogDebug("DXGI recovery attempt failed: {Msg}", ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool TryCaptureCurrentCursorShape(out CursorShapeSnapshot? snapshot, bool forceRefresh = false)
    {
        snapshot = Volatile.Read(ref _lastPointerShape);
        if (snapshot is not null && !forceRefresh)
        {
            return true;
        }

        // forceRefresh actively re-acquires the pointer shape even when one is already cached. The
        // cache is otherwise only updated by the main frame loop, which does not run while the screen
        // is static — so a cursor SHAPE change over an unchanging screen (e.g. arrow → hand over a
        // link) would never be picked up and the client would keep drawing the stale shape. The 10Hz
        // cursor sync passes forceRefresh:true to close that gap. _lock.Wait(0) means this is skipped
        // (returning the cached shape) whenever the main loop is mid-acquire, so the two never race a
        // double AcquireNextFrame on the same duplication output. (RemEx-aae)
        if (!IsAvailable || !_lock.Wait(0))
        {
            snapshot = Volatile.Read(ref _lastPointerShape);
            return snapshot is not null;
        }

        try
        {
            TryRefreshPointerShapeSnapshot();
            snapshot = Volatile.Read(ref _lastPointerShape);
            return snapshot is not null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IntPtr dxgiResource)
    {
        IntPtr frameInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_OUTDUPL_FRAME_INFO>());
        try
        {
            var hr = GetSlot<AcquireNextFrameFn>(_duplOutput, 8)(_duplOutput, timeoutMs, frameInfoPtr, out dxgiResource);
            frameInfo = hr == S_OK
                ? Marshal.PtrToStructure<DXGI_OUTDUPL_FRAME_INFO>(frameInfoPtr)
                : default;
            return hr;
        }
        finally
        {
            Marshal.FreeHGlobal(frameInfoPtr);
        }
    }

    private void TryRefreshPointerShapeSnapshot()
    {
        if (!IsAvailable)
        {
            return;
        }

        IntPtr dxgiResource = IntPtr.Zero;
        var hr = AcquireNextFrame(0, out var frameInfo, out dxgiResource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        {
            _reinitThrottle.RecordHealthyFrame(); // output alive, just no change — clears loss escalation
            return;
        }

        if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_SESSION_DISCONNECTED)
        {
            _logger.LogInformation("DXGI pointer-shape refresh lost access (hr=0x{Hr:X8}) — reinitializing.", hr);
            TryReinitializeDuplication();
            return;
        }

        if (hr != S_OK)
        {
            _logger.LogDebug("DXGI pointer-shape refresh AcquireNextFrame hr=0x{Hr:X8}", hr);
            return;
        }

        _reinitThrottle.RecordHealthyFrame(); // acquired a real frame — output confirmed healthy

        try
        {
            UpdatePointerShapeFromFrame(frameInfo);
        }
        finally
        {
            Release(ref dxgiResource);
            GetSlot<ReleaseFrameFn>(_duplOutput, 14)(_duplOutput);
        }
    }

    private void UpdatePointerShapeFromFrame(DXGI_OUTDUPL_FRAME_INFO frameInfo)
    {
        if (frameInfo.PointerShapeBufferSize == 0)
        {
            return;
        }

        IntPtr shapeBufferPtr = Marshal.AllocHGlobal((int)frameInfo.PointerShapeBufferSize);
        try
        {
            var hr = GetSlot<GetFramePointerShapeFn>(_duplOutput, 11)(
                _duplOutput,
                frameInfo.PointerShapeBufferSize,
                shapeBufferPtr,
                out var requiredBufferSize,
                out var pointerShapeInfo);

            if (hr != S_OK || requiredBufferSize == 0)
            {
                _logger.LogDebug("GetFramePointerShape hr=0x{Hr:X8}, required={Required}", hr, requiredBufferSize);
                return;
            }

            var rawShapeBytes = new byte[requiredBufferSize];
            Marshal.Copy(shapeBufferPtr, rawShapeBytes, 0, (int)requiredBufferSize);

            var shapeInfo = new DxgiPointerShapeDecoder.PointerShapeInfo(
                pointerShapeInfo.Type,
                (int)pointerShapeInfo.Width,
                (int)pointerShapeInfo.Height,
                (int)pointerShapeInfo.Pitch,
                pointerShapeInfo.HotSpot.X,
                pointerShapeInfo.HotSpot.Y);

            if (DxgiPointerShapeDecoder.TryDecode(shapeInfo, rawShapeBytes, out var snapshot))
            {
                Volatile.Write(ref _lastPointerShape, snapshot);
                return;
            }

            if (WindowsCursorShapeCapture.TryCaptureCurrentShape(out var fallbackSnapshot) && fallbackSnapshot is not null)
            {
                Volatile.Write(ref _lastPointerShape, fallbackSnapshot);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(shapeBufferPtr);
        }
    }

    // ── Cursor drawing ────────────────────────────────────────────────────────

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Draws the system cursor at its current position onto a Bitmap.
    /// </summary>
    private static void DrawCursorOnBitmap(Bitmap bitmap, int originX, int originY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0)
            return;

        if (GetIconInfo(ci.hCursor, out var iconInfo))
        {
            // ptScreenPos is in virtual-desktop coordinates; the bitmap is the captured monitor whose
            // top-left is (originX, originY). Subtract the origin so the cursor lands correctly on
            // non-primary monitors (origin (0,0) on the primary, so this is a no-op there).
            int drawX = ci.ptScreenPos.X - originX - iconInfo.xHotspot;
            int drawY = ci.ptScreenPos.Y - originY - iconInfo.yHotspot;

            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);

            using var g = Graphics.FromImage(bitmap);
            IntPtr hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, drawX, drawY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    private void ReleaseAll()
    {
        // Release in reverse dependency order: staging texture → duplication → context → device
        Release(ref _stagingTexture);
        _stagingHasFrame = false;
        Release(ref _duplOutput);
        Release(ref _d3dContext);
        Release(ref _d3dDevice);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Wait(); // Ensure no capture is in progress
        try { ReleaseAll(); }
        finally { _lock.Release(); _lock.Dispose(); }
    }
}
