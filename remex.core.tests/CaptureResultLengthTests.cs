using System;
using System.IO;
using System.Runtime.CompilerServices;
using Remex.Core.Services;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that a capture result carries the FRAME length, not the buffer's capacity (RemEx-hgox).
/// </summary>
/// <remarks>
/// <para>
/// The JPEG encoders hand back <c>MemoryStream.GetBuffer()</c> rather than <c>ToArray()</c>, which
/// avoids a Large Object Heap allocation per frame — but that buffer is bigger than the frame,
/// because a <c>MemoryStream</c> grows by doubling. Everything downstream reads the length: the frame
/// header declares it, and the WebSocket send ships exactly that many bytes.
/// </para>
/// <para>
/// THE FAILURE MODE IS SILENT, WHICH IS WHY THIS IS PINNED. Converting the payload back to a
/// <c>byte[]</c> anywhere on the path compiles, runs, and ships the zero-padded tail to the client —
/// and on the DXGI path that frame is CACHED and replayed while the desktop is unchanged, so it would
/// not be one bad frame but the same bad frame indefinitely.
/// </para>
/// </remarks>
public class CaptureResultLengthTests
{
    [Fact]
    public void AResultOverAnOversizedBufferReportsTheFrameLength()
    {
        // Exactly what the encoders do: write a frame into a stream that has grown past it, then hand
        // over the buffer with an explicit length.
        // Capacity set explicitly rather than left to the growth policy: a fresh MemoryStream sizes
        // its first buffer to max(256, count), so a single 1000-byte write produces a buffer of
        // exactly 1000 and the oversize this test is about never happens. (The assertion below
        // caught that while this test was being written, which is why it is worth keeping.)
        using var stream = new MemoryStream();
        stream.Capacity = 4096;
        stream.Write(new byte[1000]);
        var buffer = stream.GetBuffer();

        Assert.True(buffer.Length > stream.Length,
            "this test is meaningless unless the stream's buffer really is larger than its content");

        var result = new ScreenCaptureResult(buffer.AsMemory(0, (int)stream.Length), isLive: true);

        Assert.Equal(1000, result.Pixels.Length);
        Assert.NotEqual(buffer.Length, result.Pixels.Length);
    }

    [Fact]
    public void AnEmptyCaptureIsEmptyRatherThanNull()
    {
        // The type changed from byte[]? to ReadOnlyMemory<byte>, so "no frame" is now Empty rather
        // than null. Callers test IsEmpty; a default-constructed result must satisfy that.
        Assert.True(default(ScreenCaptureResult).Pixels.IsEmpty);
        Assert.True(new ScreenCaptureResult(ReadOnlyMemory<byte>.Empty, isLive: false).Pixels.IsEmpty);
    }

    /// <summary>
    /// No JPEG capture site has gone back to <c>ToArray()</c>, and none exposes the raw buffer.
    /// </summary>
    /// <remarks>
    /// The behavioural test above cannot catch this: it exercises the type, not the producers, and
    /// the producers need Direct3D or GDI to run at all. A source check is what is available, and the
    /// regression it guards is a one-word edit that compiles.
    /// </remarks>
    [Fact]
    public void NoJpegCaptureSiteReturnsAWholeBufferOrACopy()
    {
        var agent = Path.Combine(RepoRoot(), "remex.agent", "Services", "ScreenCapture");
        foreach (var name in new[]
                 {
                     "WindowsScreenCaptureService.cs",
                     "DxgiDesktopCapture.cs",
                     "LinuxScreenCaptureService.cs",
                 })
        {
            var path = Path.Combine(agent, name);
            Assert.True(File.Exists(path), $"{name} has moved; re-point this guard");
            var source = File.ReadAllText(path);

            Assert.False(source.Contains(".ToArray()", StringComparison.Ordinal)
                    && source.Contains("GetJpegEncoder", StringComparison.Ordinal)
                    && source.Contains("Save(", StringComparison.Ordinal)
                    && CountOf(source, "GetBuffer()") == 0,
                $"{name} encodes JPEG and uses ToArray with no GetBuffer, which allocates a second "
                + "frame-sized array per frame — on the LOH at MJPEG sizes.");

            // COUNTED, not merely present. A file-level "contains the correct form somewhere" check
            // passes while a SECOND site regresses to the unsliced form, and this file has two.
            Assert.Equal(CountOf(source, "GetBuffer()"), CountOf(source, "GetBuffer().AsMemory("));
        }
    }

    /// <summary>Occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
