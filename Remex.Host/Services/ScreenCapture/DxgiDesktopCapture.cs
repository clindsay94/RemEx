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

    public int Width  { get; private set; }
    public int Height { get; private set; }
    public bool IsAvailable => _duplOutput != IntPtr.Zero && !_disposed;

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
    private delegate void CopyResourceFn(IntPtr self, IntPtr pDst, IntPtr pSrc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  MapFn(IntPtr self, IntPtr pResource, uint Subresource, int MapType, uint MapFlags, IntPtr pMappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapFn(IntPtr self, IntPtr pResource, uint Subresource);

    // ── Structs ───────────────────────────────────────────────────────────────

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
            _logger.LogInformation("DXGI Desktop Duplication initialized ({W}×{H}).", Width, Height);
        }
        catch (Exception ex)
        {
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
    public byte[]? TryCapture(int quality, double scale, ImageCodecInfo jpegEncoder)
    {
        if (!IsAvailable) return null;

        // Non-blocking: if another capture is already in progress, return last frame immediately.
        // This prevents queue buildup for high-FPS streams with multiple concurrent connections.
        if (!_lock.Wait(0))
            return _lastFrame;

        try
        {
            return CaptureInternal(quality, scale, jpegEncoder);
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

    private byte[]? CaptureInternal(int quality, double scale, ImageCodecInfo jpegEncoder)
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
            _lastFrame = EncodeToJpeg(mapped.pData, (int)mapped.RowPitch, Width, Height, quality, scale, jpegEncoder);
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
        int quality, double scale, ImageCodecInfo jpegEncoder)
    {
        // System.Drawing Bitmap wraps the DXGI-mapped BGRA memory (no pixel copy needed).
        // DXGI_FORMAT_B8G8R8A8_UNORM bytes are laid out as BGRA — matches Format32bppArgb
        // on little-endian Windows (despite the name, GDI+ stores BGRA in memory).
        using var src = new Bitmap(width, height, rowPitch, PixelFormat.Format32bppArgb, pixelData);

        Bitmap output;
        if (scale < 1.0)
        {
            int sw = (int)(width * scale);
            int sh = (int)(height * scale);
            output = new Bitmap(sw, sh);
            using var g = Graphics.FromImage(output);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(src, 0, 0, sw, sh);
        }
        else
        {
            output = src;
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
            if (!ReferenceEquals(output, src))
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
            _logger.LogInformation("DXGI Desktop Duplication reinitialized ({W}×{H}).", Width, Height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DXGI reinitialize failed: {Msg}. Capture will fall back to GDI.", ex.Message);
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
