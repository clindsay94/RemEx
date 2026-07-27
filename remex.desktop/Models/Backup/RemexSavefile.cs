using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.Models.Backup;

/// <summary>
/// The root envelope of a RemEx savefile (<c>.remexsave</c>): a single, indented, camelCase
/// UTF-8 JSON document capturing everything a reinstall or new install needs to restore the
/// user's PC-side state. Never contains secret material (certificates, paired-client records,
/// pinned host certificates, or the keep-session-unlocked flag) — those are excluded by
/// construction because <see cref="RemexSavefileSections"/> simply has no field for them.
/// </summary>
public sealed class RemexSavefile
{
    /// <summary>
    /// The highest savefile format this build of RemEx understands. Bump this — and add
    /// migration logic — whenever <see cref="RemexSavefileSections"/> changes shape in a way
    /// older readers couldn't tolerate.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// Format revision of this specific file. Readers refuse files newer than
    /// <see cref="CurrentFormatVersion"/>. Deliberately has no default value: a savefile whose
    /// JSON omits this property must deserialize to 0 (not silently to
    /// <see cref="CurrentFormatVersion"/>) so <c>RemexSavefileService.ValidateFormatVersion</c>
    /// correctly refuses it as unrecognizable rather than than importing it as version 1.
    /// Writers (see <c>RemexSavefileService.BuildSavefileAsync</c>) always set this explicitly.
    /// </summary>
    public int FormatVersion { get; set; }

    /// <summary>UTC timestamp of when this savefile was produced.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>RemEx application version that produced this savefile (informational only).</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Producing OS: "windows", "linux", or "other". Informational only — import is allowed cross-platform.</summary>
    public string Os { get; set; } = string.Empty;

    /// <summary>Either "manual" (user-initiated Export) or "autosnapshot" (silent rolling backup).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The actual state payload. Any member may be null/absent — importers skip missing sections silently.</summary>
    public RemexSavefileSections Sections { get; set; } = new();
}

/// <summary>
/// The restorable state sections of a <see cref="RemexSavefile"/>. Every member is optional so
/// that older or partially-populated savefiles still import whatever they do contain.
/// </summary>
public sealed class RemexSavefileSections
{
    /// <summary>The desktop-side dashboard profile (theme, canvas layout, language, sensors, WOL, stream quality, etc.) — same shape as <c>dashboard_layout.json</c>.</summary>
    public DashboardProfile? DashboardLayout { get; set; }

    /// <summary>App Launcher entries — same shape as <c>launchers.json</c>.</summary>
    public List<AppEntry>? Launchers { get; set; }

    /// <summary>Configured File Transfer shared folder roots — same shape as <c>file_transfer_roots.json</c>.</summary>
    public List<FileTransferRootConfiguration>? FileTransferRoots { get; set; }

    /// <summary>The host-side dashboard profile — same shape as <c>host_dashboard_layout.json</c>.</summary>
    public DashboardProfile? HostDashboardLayout { get; set; }
}

/// <summary>
/// Outcome of <see cref="Remex.Desktop.Services.Backup.RemexSavefileService.ImportAsync"/>:
/// which sections were applied, which were absent from the file and therefore skipped, and any
/// non-fatal warnings raised while applying the sections that were present.
/// </summary>
public sealed record SavefileImportResult(
    List<string> AppliedSections,
    List<string> SkippedSections,
    List<string> Warnings);
