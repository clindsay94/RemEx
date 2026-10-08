using System.Text;

namespace Remex.Desktop.Services.FilePreview;

/// <summary>
/// Turns the raw bytes of a text preview into a string (2026-10-08 redesign). Pure, so the edge cases are
/// unit-tested: a byte-order mark, a read that stopped in the middle of a multi-byte character, and a tail
/// that started in the middle of a line.
/// </summary>
public static class TextPreviewDecoder
{
    /// <summary>How many leading bytes are checked for NUL when deciding whether a file is binary.</summary>
    public const int SniffBytes = 8 * 1024;

    /// <summary>
    /// True when <paramref name="bytes"/> look binary: a NUL in the first <see cref="SniffBytes"/>. UTF-16 text
    /// has NULs too, so a UTF-16 byte-order mark is checked first and wins.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        if (StartsWithUtf16Bom(bytes))
            return false;
        var head = bytes[..Math.Min(bytes.Length, SniffBytes)];
        return head.IndexOf((byte)0) >= 0;
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/>: UTF-8 (with or without a BOM) or UTF-16 by its BOM, invalid sequences
    /// shown as U+FFFD rather than refused.
    /// </summary>
    /// <param name="startsMidFile">
    /// True when the bytes are a tail, not the start of the file. The first, probably partial, line is then
    /// dropped, along with any continuation bytes of a character that began before the read.
    /// </param>
    /// <param name="endsMidFile">
    /// True when the file goes on past these bytes. A multi-byte character cut by the end of the read is then
    /// dropped instead of becoming U+FFFD (the next read starts with it whole).
    /// </param>
    public static string Decode(ReadOnlySpan<byte> bytes, bool startsMidFile = false, bool endsMidFile = false)
    {
        if (!startsMidFile)
        {
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
                return Encoding.Unicode.GetString(bytes[2..]);
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
                return Encoding.BigEndianUnicode.GetString(bytes[2..]);
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                bytes = bytes[3..];
        }

        if (endsMidFile)
            bytes = bytes[..CompleteUtf8Length(bytes)];

        if (startsMidFile)
        {
            var newline = bytes.IndexOf((byte)'\n');
            bytes = newline >= 0 ? bytes[(newline + 1)..] : [];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The length of <paramref name="bytes"/> without a trailing, incomplete UTF-8 sequence. Looks back at most
    /// three bytes, which is as far as a sequence can be cut.
    /// </summary>
    public static int CompleteUtf8Length(ReadOnlySpan<byte> bytes)
    {
        var n = bytes.Length;
        for (var back = 1; back <= 3 && back <= n; back++)
        {
            var b = bytes[n - back];
            if ((b & 0b1100_0000) == 0b1000_0000)
                continue; // a continuation byte: keep looking for the lead
            var need = (b & 0b1000_0000) == 0 ? 1
                : (b & 0b1110_0000) == 0b1100_0000 ? 2
                : (b & 0b1111_0000) == 0b1110_0000 ? 3
                : (b & 0b1111_1000) == 0b1111_0000 ? 4
                : 1; // not a valid lead: leave it for the decoder to replace
            return back < need ? n - back : n;
        }

        return n;
    }

    private static bool StartsWithUtf16Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]);
}
