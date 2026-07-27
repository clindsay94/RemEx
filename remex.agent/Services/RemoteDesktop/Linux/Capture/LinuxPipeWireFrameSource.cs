using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.RemoteDesktop.Linux;

namespace Remex.Agent.Services.RemoteDesktop.Linux.Capture;

/// <summary>
/// P/Invoke bindings to <c>libremex_linux_bridge.so</c> for PipeWire screen capture.
/// All calls are Linux-only. The native library is loaded lazily; if unavailable,
/// all methods return an error code and the managed layer falls back gracefully.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxNativePipeWire
{
    private const string LibName = "remex_linux_bridge";

    static LinuxNativePipeWire()
    {
        LinuxNativeBridgeLocator.EnsureDllImportResolverRegistered();
    }

    [DllImport(LibName, EntryPoint = "remex_pw_session_create")]
    public static extern int SessionCreate(uint nodeId, out IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_pw_session_create_v2")]
    public static extern int SessionCreateV2(
        [MarshalAs(UnmanagedType.LPStr)] string? portalSessionHandle,
        uint nodeId,
        out IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_pw_session_create_v3")]
    public static extern int SessionCreateV3(
        int pipeWireFd,
        uint nodeId,
        out IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_pw_session_acquire_frame")]
    public static extern int AcquireFrame(IntPtr handle, out LinuxFrameBufferDescriptor descriptor, int timeoutMs);

    [DllImport(LibName, EntryPoint = "remex_pw_session_release_frame")]
    public static extern void ReleaseFrame(IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_pw_session_destroy")]
    public static extern void SessionDestroy(IntPtr handle);

    [DllImport(LibName, EntryPoint = "remex_probe_capabilities")]
    public static extern int ProbeCapabilities(
        [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder buf,
        nuint bufSize);
}

[SupportedOSPlatform("linux")]
public enum LinuxPipeWireOpenFailureKind
{
    None = 0,
    NativeLibraryNotFound,
    SessionCreateFailed,
    UnexpectedError,
}

/// <summary>
/// Managed wrapper around the native PipeWire capture session.
/// Provides a simple pull-based frame acquisition interface:
///   <see cref="AcquireFrameAsync"/> blocks until a new frame arrives or times out.
///   <see cref="ReleaseFrame"/> must be called after consuming each frame.
///
/// If the native library is not present, all operations return null/false and
/// callers should fall back to the legacy shell-tool path.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPipeWireFrameSource : IDisposable
{
    private readonly ILogger<LinuxPipeWireFrameSource> _logger;
    private IntPtr _nativeHandle = IntPtr.Zero;
    private bool _nativeAvailable;
    private bool _disposed;

    public bool IsNativeAvailable => _nativeAvailable;
    public uint NodeId { get; private init; }
    public LinuxPipeWireOpenFailureKind LastOpenFailure { get; private set; }
    public int? LastOpenErrorCode { get; private set; }
    public string? LastOpenErrorMessage { get; private set; }

    /// <summary>
    /// D-Bus object path of the portal session (e.g. returned by PortalStartResult.SessionHandle).
    /// When set, TryOpen calls remex_pw_session_create_v2 so the native bridge can call
    /// OpenPipeWireRemote and get the portal-scoped PipeWire fd required on KDE/GNOME Wayland.
    /// Null falls back to remex_pw_session_create (direct daemon connection).
    /// </summary>
    public string? PortalSessionHandle { get; private init; }

    /// <summary>
    /// Portal-scoped PipeWire fd obtained by the managed portal service on the
    /// session-owning D-Bus connection. When &gt;= 0, TryOpen uses the v3 native entry
    /// point (the native bridge dup()s it). -1 falls back to v2 (native sd-bus) / v1.
    /// </summary>
    public int PipeWireFd { get; private init; } = -1;

    public LinuxPipeWireFrameSource(
        uint nodeId,
        string? portalSessionHandle = null,
        int pipeWireFd = -1,
        ILogger<LinuxPipeWireFrameSource>? logger = null)
    {
        NodeId = nodeId;
        PortalSessionHandle = portalSessionHandle;
        PipeWireFd = pipeWireFd;
        _logger = logger ?? NullLogger<LinuxPipeWireFrameSource>.Instance;
    }

    /// <summary>
    /// Opens the PipeWire session for the given node ID.
    /// Returns false when the native library is unavailable.
    /// </summary>
    public bool TryOpen()
    {
        if (_disposed) return false;

        LastOpenFailure = LinuxPipeWireOpenFailureKind.None;
        LastOpenErrorCode = null;
        LastOpenErrorMessage = null;

        try
        {
            // Prefer v3 (caller-provided portal fd on the session-owning connection) —
            // this is what makes the ACL-protected screencast node visible on KDE/GNOME
            // and actually deliver frames. Fall back to v2 (native sd-bus) then v1.
            string variant;
            int rc;
            if (PipeWireFd >= 0)
            {
                variant = "_v3";
                rc = LinuxNativePipeWire.SessionCreateV3(PipeWireFd, NodeId, out _nativeHandle);
            }
            else if (PortalSessionHandle is not null)
            {
                variant = "_v2";
                rc = LinuxNativePipeWire.SessionCreateV2(PortalSessionHandle, NodeId, out _nativeHandle);
            }
            else
            {
                variant = string.Empty;
                rc = LinuxNativePipeWire.SessionCreate(NodeId, out _nativeHandle);
            }

            if (rc != 0)
            {
                LastOpenFailure = LinuxPipeWireOpenFailureKind.SessionCreateFailed;
                LastOpenErrorCode = rc;
                LastOpenErrorMessage = $"remex_pw_session_create{variant} returned {rc}";
                _logger.LogWarning(
                    "remex_pw_session_create{Suffix} returned {Code}. PipeWire native path unavailable.",
                    variant, rc);
                _nativeAvailable = false;
                return false;
            }

            _nativeAvailable = true;
            _logger.LogInformation(
                "PipeWire capture session opened for node {NodeId} (path={Variant}, portalFd={HasFd}).",
                NodeId, variant.Length == 0 ? "direct" : variant.TrimStart('_'), PipeWireFd >= 0);
            return true;
        }
        catch (DllNotFoundException ex)
        {
            LastOpenFailure = LinuxPipeWireOpenFailureKind.NativeLibraryNotFound;
            LastOpenErrorMessage = ex.Message;
            _logger.LogWarning(ex,
                "libremex_linux_bridge.so not found. PipeWire capture unavailable.");
            _nativeAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            LastOpenFailure = LinuxPipeWireOpenFailureKind.UnexpectedError;
            LastOpenErrorMessage = ex.Message;
            _logger.LogError(ex, "Unexpected error opening PipeWire session.");
            _nativeAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Acquires the latest frame from PipeWire. Blocks up to <paramref name="timeoutMs"/> ms.
    /// Returns null on timeout, library unavailability, or error.
    /// </summary>
    public LinuxFrameSnapshot? AcquireFrame(int timeoutMs = 50)
    {
        if (!_nativeAvailable || _disposed) return null;

        int rc = LinuxNativePipeWire.AcquireFrame(
            _nativeHandle, out var descriptor, timeoutMs);

        if (rc == -4 /* REMEX_ERR_NO_FRAME */) return null;  // Timeout — normal
        if (rc != 0)
        {
            _logger.LogDebug("AcquireFrame returned error code {Code}.", rc);
            return null;
        }

        if (!descriptor.IsValid) return null;

        return BuildSnapshot(descriptor);
    }

    /// <summary>
    /// Asynchronous wrapper around <see cref="AcquireFrame"/>.
    /// Offloads the blocking wait to a thread-pool thread.
    /// </summary>
    public Task<LinuxFrameSnapshot?> AcquireFrameAsync(
        int timeoutMs = 50,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => AcquireFrame(timeoutMs), ct);
    }

    /// <summary>
    /// Returns the current frame buffer to PipeWire.
    /// Must be called after processing each frame returned by <see cref="AcquireFrame"/>.
    /// </summary>
    public void ReleaseFrame()
    {
        if (_nativeAvailable && !_disposed)
            LinuxNativePipeWire.ReleaseFrame(_nativeHandle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_nativeHandle != IntPtr.Zero)
        {
            try { LinuxNativePipeWire.SessionDestroy(_nativeHandle); }
            catch { /* best-effort */ }
            _nativeHandle = IntPtr.Zero;
        }
        _nativeAvailable = false;
    }

    // ── Private helpers ────────────────────────────────────────────────

    private static LinuxFrameSnapshot BuildSnapshot(in LinuxFrameBufferDescriptor desc)
    {
        byte[]? copy = null;

        if (desc.Kind == LinuxBufferKind.Memfd && desc.Data != IntPtr.Zero && desc.Size > 0)
        {
            // Rent from ArrayPool to avoid GC allocations/pauses.
            copy = System.Buffers.ArrayPool<byte>.Shared.Rent((int)desc.Size);
            Marshal.Copy(desc.Data, copy, 0, (int)desc.Size);
        }

        return new LinuxFrameSnapshot
        {
            Width = desc.Width,
            Height = desc.Height,
            Stride = desc.Stride,
            Format = desc.Format,
            TimestampNs = desc.TimestampNs,
            Seq = desc.Seq,
            BufferKind = desc.Kind,
            Data = copy,
            RawData = desc.Data,
            DmaBufFd = desc.Kind == LinuxBufferKind.DmaBuf ? desc.Fd : -1,
        };
    }
}
