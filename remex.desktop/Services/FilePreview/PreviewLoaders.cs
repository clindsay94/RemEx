using System.Text;
using Remex.Core.Models;

namespace Remex.Desktop.Services.FilePreview;

/// <summary>
/// Reads one range of the previewed file: from <c>offset</c>, or the last <c>length</c> bytes when
/// <c>fromEnd</c> is true. In the app this is <see cref="FileTransfer.FileTransferClient.ReadRangeRemoteAsync"/>
/// bound to one root and path; tests supply an in-memory file.
/// </summary>
public delegate Task<FileReadRangeResponse> RangeReader(long offset, int length, bool fromEnd, CancellationToken ct);

/// <summary>The file is bigger than a preview will fetch; the screen offers "Download" instead.</summary>
public sealed class PreviewTooLargeException(long fileSize, long limit)
    : IOException($"The file is {fileSize} bytes; previews stop at {limit}.")
{
    public long FileSize { get; } = fileSize;
    public long Limit { get; } = limit;
}

/// <summary>Fetches a whole image for the full-size preview, in 1 MiB reads (2026-10-08 redesign).</summary>
public static class ImagePreviewLoader
{
    /// <summary>
    /// All of the file's bytes, read in order. Refuses before fetching more than the first read when the file
    /// is over <paramref name="maxBytes"/>, and refuses a host that stops making progress rather than looping.
    /// </summary>
    /// <param name="progress">0..1 of the bytes fetched so far.</param>
    public static async Task<byte[]> LoadAsync(
        RangeReader read, IProgress<double>? progress, CancellationToken ct,
        long maxBytes = FileTransferLimits.PreviewImageMaxBytes)
    {
        var first = await read(0, FileTransferLimits.ReadRangeMaxBytes, false, ct);
        if (first.FileSize > maxBytes)
            throw new PreviewTooLargeException(first.FileSize, maxBytes);

        using var buffer = new MemoryStream(capacity: (int)Math.Max(0, first.FileSize));
        var chunk = first;
        while (true)
        {
            var data = Convert.FromBase64String(chunk.DataBase64 ?? string.Empty);
            if (chunk.Offset != buffer.Length)
                throw new IOException("The host returned bytes out of order.");
            buffer.Write(data);
            if (buffer.Length > maxBytes)
                throw new PreviewTooLargeException(buffer.Length, maxBytes);
            if (chunk.FileSize > 0)
                progress?.Report(Math.Clamp((double)buffer.Length / chunk.FileSize, 0, 1));
            if (chunk.Eof)
                break;
            if (data.Length == 0)
                throw new IOException("The host stopped sending before the end of the file.");
            chunk = await read(buffer.Length, FileTransferLimits.ReadRangeMaxBytes, false, ct);
        }

        progress?.Report(1);
        return buffer.ToArray();
    }
}

/// <summary>The start of a text file, decoded for the preview.</summary>
public sealed record TextPreview(string Text, bool IsBinary, bool Truncated, long FileSize);

/// <summary>Fetches and decodes the start of a text file, up to 2 MiB (2026-10-08 redesign).</summary>
public static class TextPreviewLoader
{
    /// <summary>
    /// The first <paramref name="maxBytes"/> of the file, decoded. Stops after the first read when its leading
    /// bytes look binary, so a mislabelled 2 GB file costs one read, not two megabytes.
    /// </summary>
    public static async Task<TextPreview> LoadHeadAsync(
        RangeReader read, CancellationToken ct, int maxBytes = FileTransferLimits.PreviewTextMaxBytes)
    {
        var bytes = new List<byte>();
        long offset = 0;
        FileReadRangeResponse chunk;
        do
        {
            var want = Math.Min(FileTransferLimits.ReadRangeMaxBytes, maxBytes - bytes.Count);
            chunk = await read(offset, want, false, ct);
            var data = Convert.FromBase64String(chunk.DataBase64 ?? string.Empty);
            if (offset == 0 && TextPreviewDecoder.LooksBinary(data))
                return new TextPreview(string.Empty, IsBinary: true, Truncated: false, chunk.FileSize);
            bytes.AddRange(data);
            offset += data.Length;
            if (data.Length == 0) break;
        }
        while (!chunk.Eof && bytes.Count < maxBytes);

        var truncated = !chunk.Eof;
        var text = TextPreviewDecoder.Decode(bytes.ToArray(), startsMidFile: false, endsMidFile: truncated);
        return new TextPreview(text, IsBinary: false, truncated, chunk.FileSize);
    }
}

/// <summary>What one live-tail step changed.</summary>
/// <param name="Reset">True when the file shrank or was replaced: throw away what is shown and use <paramref name="Lines"/>.</param>
/// <param name="Lines">Complete lines to append (or, on a reset, the whole visible tail).</param>
/// <param name="PendingLine">The last line so far, still being written (no newline yet); replaces the previous one.</param>
public sealed record TailUpdate(bool Reset, IReadOnlyList<string> Lines, string PendingLine, long FileSize);

/// <summary>
/// Live tail for a growing text file (2026-10-08 redesign): starts at the last 256 KiB, then on every poll
/// reads only what was added since. A file that shrinks (rotated, truncated, replaced) restarts from its new
/// tail. A line still being written is shown as pending and finished once its newline arrives, and a
/// multi-byte character split across two polls is never shown as U+FFFD.
/// </summary>
public sealed class TextTail(RangeReader read, int tailBytes = FileTransferLimits.PreviewTextTailBytes)
{
    private long _nextOffset;
    private byte[] _carry = [];
    private bool _started;

    /// <summary>Reads the tail and returns it as a reset. Safe to call again to start over.</summary>
    public async Task<TailUpdate> StartAsync(CancellationToken ct)
    {
        var chunk = await read(0, tailBytes, true, ct);
        var data = Convert.FromBase64String(chunk.DataBase64 ?? string.Empty);
        _started = true;
        _carry = [];
        _nextOffset = chunk.Offset + data.Length;
        // A tail that starts mid-file drops its first, partial line (Decode with startsMidFile).
        if (chunk.Offset > 0)
        {
            var newline = Array.IndexOf(data, (byte)'\n');
            data = newline >= 0 ? data[(newline + 1)..] : [];
        }
        var (lines, pending) = Split(data);
        return new TailUpdate(Reset: true, lines, pending, chunk.FileSize);
    }

    /// <summary>Reads what was added since the last call (or restarts when the file shrank).</summary>
    public async Task<TailUpdate> PollAsync(CancellationToken ct)
    {
        if (!_started)
            return await StartAsync(ct);

        var chunk = await read(_nextOffset, FileTransferLimits.ReadRangeMaxBytes, false, ct);
        if (chunk.FileSize < _nextOffset)
            return await StartAsync(ct);

        var data = Convert.FromBase64String(chunk.DataBase64 ?? string.Empty);
        _nextOffset += data.Length;
        var (lines, pending) = Split([.. _carry, .. data]);
        return new TailUpdate(Reset: false, lines, pending, chunk.FileSize);
    }

    /// <summary>
    /// Complete lines (each without its newline or a trailing CR) plus the unfinished last line. The unfinished
    /// bytes are kept for the next poll, so a line or a character split across reads joins up again.
    /// </summary>
    private (IReadOnlyList<string> Lines, string Pending) Split(byte[] bytes)
    {
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        var complete = lastNewline >= 0 ? bytes[..(lastNewline + 1)] : [];
        _carry = lastNewline >= 0 ? bytes[(lastNewline + 1)..] : bytes;
        // A file that never writes a newline (a minified log, a binary written by mistake) would otherwise grow
        // the carry by a read every poll, forever. Keep only the last tailBytes of an unfinished line, starting
        // on a character boundary.
        if (_carry.Length > tailBytes)
        {
            var start = _carry.Length - tailBytes;
            while (start < _carry.Length && (_carry[start] & 0b1100_0000) == 0b1000_0000) start++;
            _carry = _carry[start..];
        }

        var lines = new List<string>();
        if (complete.Length > 0)
        {
            var text = Encoding.UTF8.GetString(complete);
            foreach (var line in text.Split('\n'))
                lines.Add(line.TrimEnd('\r'));
            lines.RemoveAt(lines.Count - 1); // the empty string after the final newline
        }

        var pending = Encoding.UTF8.GetString(_carry, 0, TextPreviewDecoder.CompleteUtf8Length(_carry)).TrimEnd('\r');
        return (lines, pending);
    }
}
