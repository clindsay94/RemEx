using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Remex.Host.Services.RemoteDesktop.Linux.Capture;

/// <summary>
/// Buffer kind reported by the native library for each captured frame.
/// </summary>
[SupportedOSPlatform("linux")]
public enum LinuxBufferKind
{
    /// <summary>
    /// Shared memory (memfd) buffer. Data is CPU-accessible via the mapped pointer.
    /// </summary>
    Memfd = 0,

    /// <summary>
    /// DMA-BUF buffer. Data is in GPU-visible memory; CPU access requires an explicit mmap.
    /// </summary>
    DmaBuf = 1,
}

/// <summary>
/// Cursor display mode for the PipeWire stream.
/// Mirrors <c>PortalCursorMode</c> but is used on the capture-side path.
/// </summary>
[SupportedOSPlatform("linux")]
public enum LinuxCursorMode
{
    Hidden   = 1,
    Embedded = 2,
    Metadata = 4,
}

/// <summary>
/// Managed mirror of the native <c>remex_frame_descriptor_t</c> struct.
/// The layout must match the C struct exactly (sequential, no padding surprises
/// on LP64 Linux since all fields align naturally).
/// </summary>
[SupportedOSPlatform("linux")]
[StructLayout(LayoutKind.Sequential)]
public struct LinuxFrameBufferDescriptor
{
    /// <summary>Buffer kind: 0 = memfd, 1 = DMA-BUF.</summary>
    public int BufferKind;

    /// <summary>File descriptor: memfd or DMA-BUF fd; -1 if not applicable.</summary>
    public int Fd;

    /// <summary>CPU-mapped pointer for memfd path; IntPtr.Zero for DMA-BUF.</summary>
    public IntPtr Data;

    /// <summary>Byte length of the mapped region.</summary>
    public nuint Size;

    public int Width;
    public int Height;

    /// <summary>Bytes per row (stride).</summary>
    public int Stride;

    /// <summary>DRM fourcc or SPA_VIDEO_FORMAT_* pixel format code.</summary>
    public uint Format;

    /// <summary>Monotonic capture timestamp in nanoseconds.</summary>
    public long TimestampNs;

    /// <summary>Monotonically increasing frame sequence number.</summary>
    public ulong Seq;

    // ── Convenience accessors ──────────────────────────────────────────

    public readonly LinuxBufferKind Kind => (LinuxBufferKind)BufferKind;

    public readonly bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// A managed snapshot of a captured frame.
/// The snapshot either holds a byte[] copy (safe for any caller lifetime) or
/// a raw pointer valid only until <see cref="LinuxPipeWireFrameSource.ReleaseFrame"/> is called.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFrameSnapshot
{
    /// <summary>
    /// Frame dimensions and metadata. Always populated.
    /// </summary>
    public required int Width  { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required uint Format { get; init; }
    public required long TimestampNs { get; init; }
    public required ulong Seq { get; init; }
    public required LinuxBufferKind BufferKind { get; init; }

    /// <summary>
    /// Byte-copied frame data. Populated when the caller requests a safe copy.
    /// Null when <see cref="RawData"/> is used instead.
    /// </summary>
    public byte[]? Data { get; init; }

    /// <summary>
    /// Raw pointer into the shared-memory region. Valid only until
    /// <see cref="LinuxPipeWireFrameSource.ReleaseFrame"/> is called.
    /// Only set for the Memfd fast path when the caller opts out of copying.
    /// </summary>
    public IntPtr RawData { get; init; }

    /// <summary>
    /// DMA-BUF file descriptor for the DMA-BUF path. -1 otherwise.
    /// </summary>
    public int DmaBufFd { get; init; } = -1;

    /// <summary>
    /// Whether this snapshot owns a heap-copied byte array that is safe to use
    /// after the frame is released.
    /// </summary>
    public bool HasSafeCopy => Data is not null;
}
