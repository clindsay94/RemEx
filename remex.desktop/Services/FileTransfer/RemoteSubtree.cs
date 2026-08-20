using Remex.Core.Models;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// A remote folder flattened into the per-file work a transfer queue can run (RemEx-q3twg): the result
/// of paging <c>file_manifest_request</c> to its end.
/// </summary>
/// <remarks>
/// <see cref="FileManifestEntry.RelativePath"/> is relative to the ROOT, so it is already the value a
/// download request wants. <see cref="BasePath"/> is the enumerated folder's own root-relative path —
/// strip it from an entry to get the path BELOW the destination folder the user picked. Keeping both
/// forms here rather than on every wire entry is deliberate; see <see cref="FileManifestEntry"/>.
/// </remarks>
public sealed record RemoteSubtree
{
    /// <summary>Root-relative path of the enumerated folder itself; empty when it is the whole root.</summary>
    public required string BasePath { get; init; }

    /// <summary>Every file and directory beneath <see cref="BasePath"/>, in pre-order.</summary>
    public required IReadOnlyList<FileManifestEntry> Entries { get; init; }

    /// <summary>Whole-subtree totals as reported by the host, or null when it did not supply them.</summary>
    public long? TotalFiles { get; init; }
    public long? TotalDirectories { get; init; }
    public long? TotalBytes { get; init; }

    /// <summary>False when the host's totals are lower bounds rather than exact counts.</summary>
    public bool TotalsComplete { get; init; }

    /// <summary>
    /// True when the host stopped short of describing the whole subtree. The listing is then INCOMPLETE
    /// and a caller must say so rather than present the partial fan-out as the folder.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Files only, in enumeration order — the transfers a caller actually enqueues.</summary>
    public IEnumerable<FileManifestEntry> Files => Entries.Where(entry => !entry.IsDirectory);

    /// <summary>
    /// The path of <paramref name="entry"/> relative to <see cref="BasePath"/>, i.e. where it belongs
    /// under the destination folder the user chose.
    /// </summary>
    public string ToDestinationRelative(FileManifestEntry entry)
        => BasePath.Length == 0 || !entry.RelativePath.StartsWith(BasePath + '/', StringComparison.Ordinal)
            ? entry.RelativePath
            : entry.RelativePath[(BasePath.Length + 1)..];
}
