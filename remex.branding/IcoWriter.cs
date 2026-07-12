namespace Remex.Branding;

/// <summary>
/// Assembles a Windows .ico (Vista+ PNG-compressed frames) directly, avoiding the Windows-only
/// System.Drawing.Common so generation stays cross-platform.
/// </summary>
public static class IcoWriter
{
    public static byte[] Build(IReadOnlyList<(int size, byte[] png)> frames)
    {
        if (frames is null || frames.Count == 0)
            throw new ArgumentException("At least one ICO frame is required.", nameof(frames));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ICONDIR
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: 1 = icon
        w.Write((ushort)frames.Count); // image count

        int offset = 6 + 16 * frames.Count; // header + directory entries
        foreach (var (size, png) in frames)
        {
            // ICONDIRENTRY (16 bytes). 256 is encoded as 0 in the single width/height bytes.
            w.Write((byte)(size >= 256 ? 0 : size)); // width
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);            // color count (0 = >256 / truecolor)
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // color planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(png.Length);         // bytes in resource
            w.Write(offset);             // offset of image data
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
            w.Write(png);

        w.Flush();
        return ms.ToArray();
    }
}
