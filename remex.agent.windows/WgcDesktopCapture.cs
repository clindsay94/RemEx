using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Services;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Remex.Agent.Windows;

/// <summary>
/// Windows.Graphics.Capture (WGC) based per-monitor screen capture, the modern driver-robust
/// replacement for the DXGI Desktop Duplication backend on Windows 10/11.
///
/// <para>
/// Headless by design: it resolves an <c>HMONITOR</c> from a Win32 device name and creates a
/// <see cref="GraphicsCaptureItem"/> directly via the <c>IGraphicsCaptureItemInterop</c> activation
/// factory — no picker UI. The session disables cursor capture (<c>IsCursorCaptureEnabled = false</c>)
/// and, where supported, the yellow capture border (<c>IsBorderRequired = false</c>).
/// </para>
///
/// <para>
/// Mirrors <c>DxgiDesktopCapture</c>'s discipline: lazy init, a non-blocking
/// <see cref="SemaphoreSlim"/> (<c>Wait(0)</c>) so concurrent callers replay the cached frame instead
/// of queueing, a cached last-good raw BGRA frame, an <c>isLive</c> flag that goes false only when the
/// session is genuinely lost, and a rate-limited recovery path. Every native call is HRESULT-checked
/// and fails soft: on any failure <see cref="IsAvailable"/> simply stays/goes false and the orchestrator
/// falls back to DXGI → GDI. The constructor never throws.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WgcDesktopCapture : IWgcCaptureSource
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── D3D11 / WinRT device state ───────────────────────────────────────────
    private IntPtr _d3dDevice = IntPtr.Zero;       // ID3D11Device*
    private IntPtr _d3dContext = IntPtr.Zero;      // ID3D11DeviceContext*
    private IDirect3DDevice? _winrtDevice;         // WinRT wrapper around the D3D device
    private IntPtr _stagingTexture = IntPtr.Zero;  // CPU-readable ID3D11Texture2D*

    // ── WGC session state ────────────────────────────────────────────────────
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private bool _sessionLost;                     // set from GraphicsCaptureItem.Closed

    private byte[]? _lastRawFrame;
    private int _stagingWidth;
    private int _stagingHeight;

    private string? _selectedDeviceName;           // last device name asked for, for recovery
    private bool _disposed;

    // Rate-limit re-creation after the session is lost (item closed / device removed). Mirrors the
    // intent of DxgiDesktopCapture's DuplicationReinitThrottle, but kept self-contained here because
    // that throttle lives in Remex.Agent (a different assembly this project must not reference).
    private DateTime _nextRecreateUtc = DateTime.MinValue;
    private static readonly TimeSpan RecreateBackoff = TimeSpan.FromSeconds(1);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DesktopLeft { get; private set; }
    public int DesktopTop { get; private set; }
    public string? OutputDeviceName { get; private set; }

    public bool IsAvailable => !_disposed && _session is not null && !_sessionLost && _d3dDevice != IntPtr.Zero;

    // ── HRESULT / D3D11 constants (mirror DxgiDesktopCapture) ─────────────────
    private const int S_OK = 0;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const int D3D11_MAP_READ = 1;

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");
    // IGraphicsCaptureSession2 carries put_IsCursorCaptureEnabled — used as a COM fallback when the
    // WinRT projection's IsCursorCaptureEnabled setter throws (older projection versions).
    private static readonly Guid IID_IGraphicsCaptureSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    // IGraphicsCaptureItem interface IID — the riid IGraphicsCaptureItemInterop::CreateForMonitor expects
    // for the returned object. NOT the GraphicsCaptureItem runtimeclass GUID (that yields E_NOINTERFACE). (RemEx-hvqv)
    private static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, int FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleDescCount, SampleDescQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    // ── COM interop interfaces ───────────────────────────────────────────────

    // IGraphicsCaptureItemInterop — headless GraphicsCaptureItem creation from an HMONITOR.
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        // CreateForWindow is slot 3 — declared so the vtable layout of CreateForMonitor (slot 4) is correct.
        [PreserveSig]
        int CreateForWindow(IntPtr window, in Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, in Guid iid, out IntPtr result);
    }

    // IDirect3DDxgiInterfaceAccess — round-trips a WinRT IDirect3DSurface back to an ID3D11Texture2D.
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out IntPtr result);
    }

    // ── vtable dispatch helpers (no unsafe code), mirroring DxgiDesktopCapture ──

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceFn(IntPtr self, ref Guid riid, out IntPtr ppvObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DFn(IntPtr self, IntPtr pDesc, IntPtr pInitialData, out IntPtr ppTex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceFn(IntPtr self, IntPtr pDst, IntPtr pSrc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapFn(IntPtr self, IntPtr pResource, uint Subresource, int MapType, uint MapFlags, IntPtr pMapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapFn(IntPtr self, IntPtr pResource, uint Subresource);

    private static T GetSlot<T>(IntPtr com, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(com);
        IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static int QueryInterface(IntPtr com, Guid iid, out IntPtr result)
    {
        var fn = GetSlot<QueryInterfaceFn>(com, 0);
        return fn(com, ref iid, out result);
    }

    private static void Release(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        GetSlot<ReleaseFn>(com, 2)(com);
        com = IntPtr.Zero;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public WgcDesktopCapture() : this(NullLogger<WgcDesktopCapture>.Instance) { }

    public WgcDesktopCapture(ILogger<WgcDesktopCapture> logger)
    {
        _logger = logger ?? NullLogger<WgcDesktopCapture>.Instance;
        // No device/session is created here. The orchestrator selects a monitor (TrySelectMonitor)
        // before the first capture; until then IsAvailable stays false. Constructor must never throw —
        // any WGC unavailability surfaces only as IsAvailable == false.
    }

    // ── Device creation ───────────────────────────────────────────────────────

    // Must be called under _lock. Idempotent: returns true if a usable D3D device + WinRT wrapper exist.
    private bool EnsureDevice()
    {
        if (_d3dDevice != IntPtr.Zero && _winrtDevice is not null) return true;

        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr graphicsDevicePtr = IntPtr.Zero;
        try
        {
            int hr = D3D11CreateDevice(
                IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out _d3dDevice, out _, out _d3dContext);
            if (hr != S_OK)
            {
                _logger.LogInformation("WGC: D3D11CreateDevice failed hr=0x{Hr:X8}.", hr);
                ReleaseDevice();
                return false;
            }

            hr = QueryInterface(_d3dDevice, IID_IDXGIDevice, out dxgiDevice);
            if (hr != S_OK)
            {
                _logger.LogInformation("WGC: QI IDXGIDevice failed hr=0x{Hr:X8}.", hr);
                ReleaseDevice();
                return false;
            }

            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevicePtr);
            if (hr != S_OK)
            {
                _logger.LogInformation("WGC: CreateDirect3D11DeviceFromDXGIDevice failed hr=0x{Hr:X8}.", hr);
                ReleaseDevice();
                return false;
            }

            // Project the IInspectable* into the WinRT IDirect3DDevice via CsWinRT. Using
            // Marshal.GetObjectForIUnknown(...) as IDirect3DDevice yields a legacy __ComObject that CsWinRT
            // cannot re-marshal — passing it to Direct3D11CaptureFramePool.CreateFreeThreaded then throws
            // "Failed to create a CCW ... IDirect3DDevice: the specified cast is not valid", silently dropping
            // WGC to DXGI/GDI. MarshalInspectable<T>.FromAbi mirrors the GraphicsCaptureItem.FromAbi pattern
            // below (it AddRefs; the caller still Releases graphicsDevicePtr in the finally). (RemEx-hvqv)
            _winrtDevice = WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsDevicePtr);
            if (_winrtDevice is null)
            {
                _logger.LogInformation("WGC: could not project IDirect3DDevice.");
                ReleaseDevice();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("WGC: device init failed ({Msg}).", ex.Message);
            ReleaseDevice();
            return false;
        }
        finally
        {
            Release(ref dxgiDevice);
            Release(ref graphicsDevicePtr);
        }
    }

    // ── Monitor selection ──────────────────────────────────────────────────────

    public bool TrySelectMonitor(string deviceName, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "Capture source disposed.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            error = "Empty device name.";
            return false;
        }

        if (!_lock.Wait(2000))
        {
            error = "Capture busy.";
            return false;
        }
        try
        {
            return SelectMonitorLocked(deviceName, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogInformation("WGC: TrySelectMonitor failed ({Msg}).", ex.Message);
            TearDownSession();
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Must be called under _lock.
    private bool SelectMonitorLocked(string deviceName, out string? error)
    {
        error = null;
        TearDownSession();

        if (!EnsureDevice())
        {
            error = "Direct3D device unavailable.";
            return false;
        }

        if (!TryResolveMonitor(deviceName, out IntPtr hMonitor, out RECT monitorRect))
        {
            error = $"Monitor '{deviceName}' not found.";
            return false;
        }

        // Create the GraphicsCaptureItem headlessly via the interop activation factory (no picker UI).
        IGraphicsCaptureItemInterop interop = GetCaptureItemInterop();

        // CreateForMonitor's riid must be the IGraphicsCaptureItem *interface* IID, not the runtimeclass
        // GUID — the latter returns E_NOINTERFACE (0x80004002) and silently drops WGC to DXGI/GDI. (RemEx-hvqv)
        int hr = interop.CreateForMonitor(hMonitor, IID_IGraphicsCaptureItem, out IntPtr itemPtr);
        if (hr != S_OK || itemPtr == IntPtr.Zero)
        {
            error = $"CreateForMonitor failed hr=0x{hr:X8}.";
            return false;
        }

        try
        {
            _item = GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Release(ref itemPtr);
        }

        if (_item is null)
        {
            error = "GraphicsCaptureItem projection failed.";
            return false;
        }

        SizeInt32 size = _item.Size;
        Width = size.Width;
        Height = size.Height;
        DesktopLeft = monitorRect.Left;
        DesktopTop = monitorRect.Top;
        OutputDeviceName = deviceName;
        _selectedDeviceName = deviceName;

        if (Width <= 0 || Height <= 0)
        {
            error = $"WGC reported zero-size monitor ({Width}x{Height}).";
            TearDownSession();
            return false;
        }

        _sessionLost = false;
        _item.Closed += OnItemClosed;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _session = _framePool.CreateCaptureSession(_item);

        bool cursorCaptureDisabled = TrySuppressCursorCapture(_session);

        // IsBorderRequired is not in the 19041 projection (it shipped in a later Universal API contract),
        // so set it through the IGraphicsCaptureSession3 COM interface, guarded for older OS where the QI
        // simply fails. Suppresses the yellow capture border on Win11/2004+.
        TrySuppressCaptureBorder(_session);

        _session.StartCapture();

        // Recreate the staging texture to match the monitor size.
        if (!EnsureStagingTexture(Width, Height))
        {
            error = "Failed to create staging texture.";
            TearDownSession();
            return false;
        }

        _logger.LogInformation(
            "WGC capture started for {Device} ({W}x{H}); cursorCaptureDisabled={Flag}.",
            deviceName, Width, Height, cursorCaptureDisabled);
        return true;
    }

    // Disables WGC's own cursor compositing so the client-side rendered cursor (position + shape
    // streamed separately) doesn't end up doubled under the host's baked-in OS cursor. Tries the
    // WinRT projection first, then falls back to the IGraphicsCaptureSession2 COM interface for
    // projection versions where the setter throws NotImplementedException.
    private bool TrySuppressCursorCapture(GraphicsCaptureSession session)
    {
        try
        {
            session.IsCursorCaptureEnabled = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("WGC: IsCursorCaptureEnabled projection setter failed ({Msg}); trying COM fallback.", ex.Message);
        }

        IntPtr unknown = IntPtr.Zero;
        IntPtr session2 = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(session);
            int hr = QueryInterface(unknown, IID_IGraphicsCaptureSession2, out session2);
            if (hr != S_OK || session2 == IntPtr.Zero)
            {
                _logger.LogWarning(
                    "WGC: could not disable cursor capture (QueryInterface hr=0x{Hr:X8}); the OS cursor will be baked into captured frames and may appear doubled on the client.",
                    hr);
                return false;
            }

            // IGraphicsCaptureSession2: get_IsCursorCaptureEnabled = 6, put_IsCursorCaptureEnabled = 7
            // (3 IUnknown + 3 IInspectable slots precede the interface's own members).
            GetSlot<PutBoolFn>(session2, 7)(session2, 0);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WGC: could not disable cursor capture ({Msg}); the OS cursor will be baked into captured frames and may appear doubled on the client.",
                ex.Message);
            return false;
        }
        finally
        {
            Release(ref session2);
            Release(ref unknown);
        }
    }

    // IGraphicsCaptureSession3::put_IsBorderRequired — vtable slot 6 (after 3 IInspectable + 3 IUnknown).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutBoolFn(IntPtr self, byte value);

    private void TrySuppressCaptureBorder(GraphicsCaptureSession session)
    {
        IntPtr unknown = IntPtr.Zero;
        IntPtr session3 = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(session);
            int hr = QueryInterface(unknown, IID_IGraphicsCaptureSession3, out session3);
            if (hr != S_OK || session3 == IntPtr.Zero)
                return; // older OS without IsBorderRequired — leave the border as-is

            // IInspectable adds 3 slots (GetIids/GetRuntimeClassName/GetTrustLevel) after IUnknown's 3,
            // so the first interface method (put_IsBorderRequired's getter pair) starts at slot 6.
            // IGraphicsCaptureSession3: get_IsBorderRequired = 6, put_IsBorderRequired = 7.
            GetSlot<PutBoolFn>(session3, 7)(session3, 0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("WGC: IsBorderRequired suppression skipped ({Msg}).", ex.Message);
        }
        finally
        {
            Release(ref session3);
            Release(ref unknown);
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _sessionLost = true;
        _logger.LogInformation("WGC: capture item closed — session lost, will attempt recovery.");
    }

    private bool TryResolveMonitor(string deviceName, out IntPtr hMonitor, out RECT monitorRect)
    {
        IntPtr found = IntPtr.Zero;
        RECT foundRect = default;

        bool Callback(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMon, ref mi) &&
                string.Equals(mi.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = hMon;
                foundRect = mi.rcMonitor;
                return false; // stop enumeration
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        hMonitor = found;
        monitorRect = foundRect;
        return found != IntPtr.Zero;
    }

    // Obtain IGraphicsCaptureItemInterop from the GraphicsCaptureItem activation factory.
    private static IGraphicsCaptureItemInterop GetCaptureItemInterop()
    {
        using WinRT.IObjectReference factory =
            WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        return factory.AsInterface<IGraphicsCaptureItemInterop>();
    }

    // ── Staging texture ────────────────────────────────────────────────────────

    // Must be called under _lock.
    private bool EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
            return true;

        Release(ref _stagingTexture);
        _stagingWidth = 0;
        _stagingHeight = 0;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDescCount = 1,
            SampleDescQuality = 0,
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        IntPtr descPtr = Marshal.AllocHGlobal(Marshal.SizeOf<D3D11_TEXTURE2D_DESC>());
        try
        {
            Marshal.StructureToPtr(desc, descPtr, false);
            // ID3D11Device::CreateTexture2D = slot 5 (after 3 IUnknown).
            int hr = GetSlot<CreateTexture2DFn>(_d3dDevice, 5)(_d3dDevice, descPtr, IntPtr.Zero, out _stagingTexture);
            if (hr != S_OK)
            {
                _logger.LogInformation("WGC: CreateTexture2D (staging) failed hr=0x{Hr:X8}.", hr);
                _stagingTexture = IntPtr.Zero;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descPtr);
        }

        _stagingWidth = width;
        _stagingHeight = height;
        return true;
    }

    // ── Capture ────────────────────────────────────────────────────────────────

    public byte[]? TryCaptureRaw(double scale, bool drawCursor, out bool isLive)
    {
        isLive = false;
        if (_disposed) return null;

        // Non-blocking: a concurrent capture is doing the real work, so replaying its cached frame is live.
        if (!_lock.Wait(0))
        {
            isLive = true;
            return _lastRawFrame;
        }

        try
        {
            if (_session is null || _d3dDevice == IntPtr.Zero)
                return null; // not selected / device gone → caller falls back to DXGI/GDI

            if (_sessionLost)
            {
                // Session is gone; return last good frame as STALE and leave re-creation to TryRecover().
                isLive = false;
                return _lastRawFrame;
            }

            return CaptureRawLocked(scale, out isLive);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("WGC capture error: {Msg}", ex.Message);
            isLive = false;
            return _lastRawFrame;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Must be called under _lock.
    private byte[]? CaptureRawLocked(double scale, out bool isLive)
    {
        isLive = false;

        Direct3D11CaptureFrame? frame = _framePool!.TryGetNextFrame();
        if (frame is null)
        {
            // No new frame but session alive = unchanged desktop. Healthy static screen → live.
            isLive = true;
            return _lastRawFrame;
        }

        IntPtr srcTex = IntPtr.Zero;
        try
        {
            // If the monitor changed size mid-stream, WGC raises a content-size-changed condition; the
            // surface still carries its real size, which we honor via the staging texture below.
            IDirect3DSurface surface = frame.Surface;
            var access = surface as IDirect3DDxgiInterfaceAccess;
            if (access is null)
            {
                isLive = false;
                return _lastRawFrame;
            }

            Guid texIid = IID_ID3D11Texture2D;
            int hr = access.GetInterface(texIid, out srcTex);
            if (hr != S_OK || srcTex == IntPtr.Zero)
            {
                isLive = false;
                return _lastRawFrame;
            }

            if (!EnsureStagingTexture(Width, Height))
            {
                isLive = false;
                return _lastRawFrame;
            }

            // ID3D11DeviceContext::CopyResource = slot 47.
            GetSlot<CopyResourceFn>(_d3dContext, 47)(_d3dContext, _stagingTexture, srcTex);
        }
        finally
        {
            Release(ref srcTex);
            frame.Dispose();
        }

        IntPtr mappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MappedSubresource>());
        // ID3D11DeviceContext::Map = slot 14.
        int mapHr = GetSlot<MapFn>(_d3dContext, 14)(_d3dContext, _stagingTexture, 0, D3D11_MAP_READ, 0, mappedPtr);
        if (mapHr != S_OK)
        {
            Marshal.FreeHGlobal(mappedPtr);
            isLive = false;
            return _lastRawFrame;
        }

        try
        {
            var mapped = Marshal.PtrToStructure<MappedSubresource>(mappedPtr);
            _lastRawFrame = EncodeToRawBgra(mapped.pData, (int)mapped.RowPitch, Width, Height, scale);
            isLive = true;
            return _lastRawFrame;
        }
        finally
        {
            // ID3D11DeviceContext::Unmap = slot 15.
            GetSlot<UnmapFn>(_d3dContext, 15)(_d3dContext, _stagingTexture, 0);
            Marshal.FreeHGlobal(mappedPtr);
        }
    }

    // Reads the mapped BGRA surface (honoring RowPitch) and scales to the even-aligned target size so
    // the buffer matches what the H.264 encoder was started with. Mirrors DxgiDesktopCapture.EncodeToRawBgra.
    private static byte[] EncodeToRawBgra(IntPtr pixelData, int rowPitch, int width, int height, double scale)
    {
        // RD-C fast path: when no resample is needed, copy the mapped surface directly — no System.Drawing,
        // no GDI+ blit, no intermediate Bitmap. The old per-frame GDI+ path is what capped the stream near
        // 30 FPS (not the hardware NVENC encoder). See BgraFrameConverter / docs/REMOTE_DESKTOP_PERFORMANCE.md.
        var fast = BgraFrameConverter.TryConvertNoScale(pixelData, rowPitch, width, height, scale);
        if (fast is not null)
        {
            return fast;
        }

        // Downscale path: keep GDI+'s native bilinear resampler (a scalar managed bilinear would be slower).
        int sw = CaptureScaling.ScaledEven(width, scale);
        int sh = CaptureScaling.ScaledEven(height, scale);

        // Wrap the mapped GPU memory as a read-only Bitmap. We must not draw into this surface (mapped READ).
        using var src = new Bitmap(width, height, rowPitch, PixelFormat.Format32bppArgb, pixelData);
        using var scaled = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(src, 0, 0, sw, sh);
        }
        return GetRawBgraBytes(scaled);
    }

    private static byte[] GetRawBgraBytes(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Tightly pack: the source bitmap stride may exceed width*4, so copy row by row.
            int rowBytes = bmp.Width * 4;
            byte[] result = new byte[rowBytes * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
            {
                IntPtr rowPtr = data.Scan0 + y * data.Stride;
                Marshal.Copy(rowPtr, result, y * rowBytes, rowBytes);
            }
            return result;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ── Recovery ────────────────────────────────────────────────────────────────

    public void TryRecover()
    {
        if (_disposed || IsAvailable) return;
        if (_selectedDeviceName is null) return;

        var now = DateTime.UtcNow;
        if (now < _nextRecreateUtc) return;

        // Non-blocking: if a capture is mid-flight, skip this attempt.
        if (!_lock.Wait(0)) return;
        try
        {
            if (_disposed || IsAvailable) return;
            now = DateTime.UtcNow;
            if (now < _nextRecreateUtc) return;
            _nextRecreateUtc = now + RecreateBackoff;

            if (SelectMonitorLocked(_selectedDeviceName, out var error))
            {
                _logger.LogInformation("WGC capture recovered for {Device}.", _selectedDeviceName);
            }
            else
            {
                _logger.LogDebug("WGC recovery attempt failed: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("WGC recovery attempt threw: {Msg}", ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Teardown / Dispose ───────────────────────────────────────────────────────

    // Must be called under _lock. Tears down WGC session state but keeps the D3D device alive.
    private void TearDownSession()
    {
        if (_item is not null)
        {
            try { _item.Closed -= OnItemClosed; } catch { /* ignore */ }
        }

        try { _session?.Dispose(); } catch { /* ignore */ }
        try { _framePool?.Dispose(); } catch { /* ignore */ }

        _session = null;
        _framePool = null;
        _item = null;
        _sessionLost = false;

        Release(ref _stagingTexture);
        _stagingWidth = 0;
        _stagingHeight = 0;
        Width = 0;
        Height = 0;
    }

    private void ReleaseDevice()
    {
        if (_winrtDevice is not null)
        {
            try { (_winrtDevice as IDisposable)?.Dispose(); } catch { /* ignore */ }
            _winrtDevice = null;
        }
        Release(ref _stagingTexture);
        Release(ref _d3dContext);
        Release(ref _d3dDevice);
        _stagingWidth = 0;
        _stagingHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Wait();
        try
        {
            TearDownSession();
            ReleaseDevice();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
