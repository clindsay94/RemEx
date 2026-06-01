using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Remex.Host.Services.ScreenCapture;

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

    private bool _disposed;

    // Throttle for recovery attempts after the duplication is lost (e.g. secure desktop / UAC /
    // lock screen returns E_ACCESSDENIED). Prevents hammering DuplicateOutput every frame.
    private DateTime _nextReinitAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan ReinitBackoff = TimeSpan.FromSeconds(1);

    public int Width  { get; private set; }
    public int Height { get; private set; }
    public int DesktopLeft { get; private set; }
    public int DesktopTop  { get; private set; }
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
        _logger = logger;
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
    public byte[]? TryCapture(int quality, double scale, ImageCodecInfo jpegEncoder, bool drawCursor = true)
    {
        if (!IsAvailable) return null;

        // Non-blocking: if another capture is already in progress, return last frame immediately.
        // This prevents queue buildup for high-FPS streams with multiple concurrent connections.
        if (!_lock.Wait(0))
            return _lastFrame;

        try
        {
            return CaptureInternal(quality, scale, jpegEncoder, drawCursor);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("DXGI capture error: {Msg}", ex.Message);
            return _lastFrame;
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[]? _lastRawFrame;

    public byte[]? TryCaptureRaw(double scale, bool drawCursor)
    {
        if (!IsAvailable) return null;

        if (!_lock.Wait(0))
            return _lastRawFrame;

        try
        {
            return CaptureRawInternal(scale, drawCursor);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("DXGI raw capture error: {Msg}", ex.Message);
            return _lastRawFrame;
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[]? CaptureRawInternal(double scale, bool drawCursor)
    {
        IntPtr frameInfoPtr = Marshal.AllocHGlobal(48);
        IntPtr dxgiResource = IntPtr.Zero;
        int hr;
        try
        {
            hr = GetSlot<AcquireNextFrameFn>(_duplOutput, 8)(_duplOutput, 50u, frameInfoPtr, out dxgiResource);
        }
        finally
        {
            Marshal.FreeHGlobal(frameInfoPtr);
        }

        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            return _lastRawFrame;

        if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_SESSION_DISCONNECTED)
        {
            _logger.LogInformation("DXGI access lost (hr=0x{Hr:X8}) — reinitializing.", hr);
            TryReinitializeDuplication();
            return _lastRawFrame;
        }

        if (hr != S_OK)
        {
            _logger.LogDebug("AcquireNextFrame hr=0x{Hr:X8}", hr);
            return _lastRawFrame;
        }

        try
        {
            hr = QueryInterface(dxgiResource, IID_ID3D11Texture2D, out var srcTex);
            if (hr != S_OK) return _lastRawFrame;

            try
            {
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
            GetSlot<ReleaseFrameFn>(_duplOutput, 14)(_duplOutput);
        }

        IntPtr mappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MappedSubresource>());
        hr = GetSlot<MapFn>(_d3dContext, 14)(
            _d3dContext, _stagingTexture, 0, D3D11_MAP_READ, 0, mappedPtr);

        if (hr != S_OK)
        {
            Marshal.FreeHGlobal(mappedPtr);
            return _lastRawFrame;
        }

        try
        {
            var mapped = Marshal.PtrToStructure<MappedSubresource>(mappedPtr);
            _lastRawFrame = EncodeToRawBgra(mapped.pData, (int)mapped.RowPitch, Width, Height, scale, drawCursor);
            return _lastRawFrame;
        }
        finally
        {
            GetSlot<UnmapFn>(_d3dContext, 15)(_d3dContext, _stagingTexture, 0);
            Marshal.FreeHGlobal(mappedPtr);
        }
    }

    private static byte[] EncodeToRawBgra(IntPtr pixelData, int rowPitch, int width, int height,
        double scale, bool drawCursor)
    {
        using var src = new Bitmap(width, height, rowPitch, PixelFormat.Format32bppArgb, pixelData);
        using var writable = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(writable))
        {
            g.DrawImage(src, 0, 0, width, height);
        }

        if (drawCursor)
        {
            DrawCursorOnBitmap(writable);
        }

        Bitmap output;
        if (scale < 1.0)
        {
            int sw = (int)(width * scale);
            int sh = (int)(height * scale);
            output = new Bitmap(sw, sh);
            using var g = Graphics.FromImage(output);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
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

    private byte[]? CaptureInternal(int quality, double scale, ImageCodecInfo jpegEncoder, bool drawCursor)
    {
        // AcquireNextFrame — slot 8 on IDXGIOutputDuplication
        // Timeout 50ms: wait up to 50ms for a new frame.
        // Returns DXGI_ERROR_WAIT_TIMEOUT if no new frame; we reuse last JPEG (desktop unchanged).
        IntPtr frameInfoPtr = Marshal.AllocHGlobal(48); // sizeof(DXGI_OUTDUPL_FRAME_INFO) = 48 bytes
        IntPtr dxgiResource = IntPtr.Zero;
        int hr;
        try
        {
            hr = GetSlot<AcquireNextFrameFn>(_duplOutput, 8)(_duplOutput, 50u, frameInfoPtr, out dxgiResource);
        }
        finally
        {
            Marshal.FreeHGlobal(frameInfoPtr);
        }

        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
            return _lastFrame; // Desktop unchanged — bandwidth-efficient reuse

        if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_SESSION_DISCONNECTED)
        {
            _logger.LogInformation("DXGI access lost (hr=0x{Hr:X8}) — reinitializing.", hr);
            TryReinitializeDuplication();
            return _lastFrame;
        }

        if (hr != S_OK)
        {
            _logger.LogDebug("AcquireNextFrame hr=0x{Hr:X8}", hr);
            return _lastFrame;
        }

        try
        {
            // QI IDXGIResource → ID3D11Texture2D
            hr = QueryInterface(dxgiResource, IID_ID3D11Texture2D, out var srcTex);
            if (hr != S_OK) return _lastFrame;

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
            return _lastFrame;
        }

        try
        {
            var mapped = Marshal.PtrToStructure<MappedSubresource>(mappedPtr);
            _lastFrame = EncodeToJpeg(mapped.pData, (int)mapped.RowPitch, Width, Height, quality, scale, jpegEncoder, drawCursor);
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
        int quality, double scale, ImageCodecInfo jpegEncoder, bool drawCursor)
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
            DrawCursorOnBitmap(writable);
        }

        Bitmap output;
        if (scale < 1.0)
        {
            int sw = (int)(width * scale);
            int sh = (int)(height * scale);
            output = new Bitmap(sw, sh);
            using var g = Graphics.FromImage(output);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
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
        // Release only the duplication + staging texture. Keep D3D device/context alive.
        Release(ref _stagingTexture);
        Release(ref _duplOutput);
        Width = 0;
        Height = 0;

        try
        {
            InitializeDuplication();
            UnavailableReason = null;
            _logger.LogInformation("DXGI Desktop Duplication reinitialized ({W}×{H}).", Width, Height);
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
            // Schedule a backoff so TryRecover() doesn't retry immediately. This is normal while a
            // secure desktop (UAC/lock) is up; recovery succeeds once the user returns to the desktop.
            _nextReinitAttemptUtc = DateTime.UtcNow + ReinitBackoff;
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
        if (DateTime.UtcNow < _nextReinitAttemptUtc) return;

        // Non-blocking: if a capture is mid-flight, skip this attempt.
        if (!_lock.Wait(0)) return;
        try
        {
            if (IsAvailable || _disposed) return;
            _nextReinitAttemptUtc = DateTime.UtcNow + ReinitBackoff;

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
    private static void DrawCursorOnBitmap(Bitmap bitmap)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0)
            return;

        if (GetIconInfo(ci.hCursor, out var iconInfo))
        {
            int drawX = ci.ptScreenPos.X - iconInfo.xHotspot;
            int drawY = ci.ptScreenPos.Y - iconInfo.yHotspot;

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
