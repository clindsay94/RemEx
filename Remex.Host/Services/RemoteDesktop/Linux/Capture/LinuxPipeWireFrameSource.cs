using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Remex.Host.Services.RemoteDesktop.Linux.Capture;

/// <summary>
/// P/Invoke bindings to <c>libremex_linux_bridge.so</c> for PipeWire screen capture.
/// All calls are Linux-only. The native library is loaded lazily; if unavailable,
/// all methods return an error code and the managed layer falls back gracefully.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxNativePipeWire
{
    private const string LibName = "remex_linux_bridge";

    [DllImport(LibName, EntryPoint = "remex_pw_session_create")]
    public static extern int SessionCreate(uint nodeId, out IntPtr handle);

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

    public LinuxPipeWireFrameSource(
        uint nodeId,
        ILogger<LinuxPipeWireFrameSource>? logger = null)
    {
        NodeId = nodeId;
        _logger = logger ?? NullLogger<LinuxPipeWireFrameSource>.Instance;
    }

    /// <summary>
    /// Opens the PipeWire session for the given node ID.
    /// Returns false when the native library is unavailable.
    /// </summary>
    public bool TryOpen()
    {
        if (_disposed) return false;

        try
        {
            int rc = LinuxNativePipeWire.SessionCreate(NodeId, out _nativeHandle);
            if (rc != 0)
            {
                _logger.LogWarning(
                    "remex_pw_session_create returned {Code}. PipeWire native path unavailable.",
                    rc);
                _nativeAvailable = false;
                return false;
            }

            _nativeAvailable = true;
            _logger.LogInformation(
                "PipeWire capture session opened for node {NodeId}.", NodeId);
            return true;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogWarning(ex,
                "libremex_linux_bridge.so not found. PipeWire capture unavailable.");
            _nativeAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
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
            // Copy into managed memory so the caller can use it after ReleaseFrame.
            copy = new byte[(int)desc.Size];
            Marshal.Copy(desc.Data, copy, 0, (int)desc.Size);
        }

        return new LinuxFrameSnapshot
        {
            Width        = desc.Width,
            Height       = desc.Height,
            Stride       = desc.Stride,
            Format       = desc.Format,
            TimestampNs  = desc.TimestampNs,
            Seq          = desc.Seq,
            BufferKind   = desc.Kind,
            Data         = copy,
            RawData      = desc.Data,
            DmaBufFd     = desc.Kind == LinuxBufferKind.DmaBuf ? desc.Fd : -1,
        };
    }
}
