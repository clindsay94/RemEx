using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// Enumerates the PC host's mounted volumes/drives for the consent-gated full-device browse feature
/// (plan §1.2 / §2, WP2). Windows uses <see cref="DriveInfo.GetDrives"/> filtered to ready drives; Linux
/// exposes the root filesystem plus real mounts parsed from <c>/proc/mounts</c>.
///
/// <para>
/// The Linux system denylist (<c>/proc</c>, <c>/sys</c>, <c>/dev</c>, <c>/run</c>, <c>/boot/efi</c>) and
/// the pseudo-filesystem filter are applied <b>unconditionally</b> inside the parser so the same rules
/// hold when the parser is exercised from a unit test on any host OS — this mirrors the plan's "Linux
/// denylist enforced unconditionally" and the OS-gated
/// <see cref="Remex.Core.Validation.FilePathValidation.IsRestrictedSystemPath"/> used at path-resolution
/// time.
/// </para>
/// </summary>
public sealed class VolumeEnumerator
{
    /// <summary>
    /// Linux mount prefixes that are never exposed as browsable volumes, even under a full-browse grant.
    /// Kept in lockstep with the denylist in <see cref="Remex.Core.Validation.FilePathValidation"/>
    /// (plan §2); duplicated here because the path-validation variant is OS-gated to Linux, whereas this
    /// parser must filter Linux mount data regardless of the host it runs on (for hermetic testing).
    /// </summary>
    private static readonly string[] RestrictedLinuxMountPrefixes =
        ["/proc", "/sys", "/dev", "/run", "/boot/efi"];

    /// <summary>Linux pseudo/virtual filesystem types that never represent a browsable volume.</summary>
    private static readonly HashSet<string> LinuxPseudoFileSystems = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "cgroup", "cgroup2", "securityfs",
        "pstore", "efivarfs", "bpf", "autofs", "mqueue", "hugetlbfs", "debugfs", "tracefs",
        "fusectl", "configfs", "ramfs", "binfmt_misc", "rpc_pipefs", "nsfs", "selinuxfs",
        "squashfs", "overlay", "fuse.gvfsd-fuse", "fuse.portal",
    };

    /// <summary>Linux filesystem types that indicate a network-backed mount.</summary>
    private static readonly HashSet<string> LinuxNetworkFileSystems = new(StringComparer.Ordinal)
    {
        "nfs", "nfs4", "cifs", "smbfs", "smb3", "ncpfs", "afs", "glusterfs", "ceph",
    };

    private readonly ILogger<VolumeEnumerator> _logger;

    public VolumeEnumerator(ILogger<VolumeEnumerator> logger)
    {
        _logger = Guard.NotNull(logger);
    }

    /// <summary>
    /// Returns the mounted volumes for the current OS. Empty on unsupported platforms. Callers must gate
    /// this behind a full-browse consent grant (see <c>FileTransferHandler.HandleFileVolumesRequestAsync</c>).
    /// </summary>
    public IReadOnlyList<FileVolumeInfo> Enumerate()
    {
        if (OperatingSystem.IsWindows())
            return EnumerateWindows();
        if (OperatingSystem.IsLinux())
            return EnumerateLinux();
        return Array.Empty<FileVolumeInfo>();
    }

    private IReadOnlyList<FileVolumeInfo> EnumerateWindows()
    {
        var volumes = new List<FileVolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var root = drive.RootDirectory.FullName;

                string label;
                try
                {
                    label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    label = drive.Name;
                }

                volumes.Add(new FileVolumeInfo
                {
                    Id = root,
                    Label = label,
                    Path = root,
                    TotalBytes = SafeSize(() => drive.TotalSize),
                    FreeBytes = SafeSize(() => drive.AvailableFreeSpace),
                    Kind = MapWindowsKind(drive.DriveType),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping unreadable drive during volume enumeration.");
            }
        }
        return volumes;
    }

    private IReadOnlyList<FileVolumeInfo> EnumerateLinux()
    {
        string mounts;
        try
        {
            mounts = File.ReadAllText("/proc/mounts");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to read /proc/mounts; exposing the root volume only.");
            mounts = string.Empty;
        }

        return ParseLinuxMounts(mounts, ProbeLinuxSpace);
    }

    private static (long total, long free) ProbeLinuxSpace(string mountPoint)
    {
        try
        {
            var di = new DriveInfo(mountPoint);
            return (di.TotalSize, di.AvailableFreeSpace);
        }
        catch
        {
            // Space is best-effort metadata; a probe failure must not drop the volume.
            return (0, 0);
        }
    }

    /// <summary>
    /// Pure parser for <c>/proc/mounts</c> content. Always yields the root filesystem (<c>/</c>, kind
    /// "root"), then every real mount that is not a pseudo filesystem and not under the restricted
    /// denylist. <paramref name="spaceProbe"/> supplies total/free bytes for a mount point (null → zero,
    /// used by tests). Deduplicates by mount point. Static and OS-independent so it is unit-testable on
    /// any host.
    /// </summary>
    internal static IReadOnlyList<FileVolumeInfo> ParseLinuxMounts(
        string procMountsContent,
        Func<string, (long total, long free)>? spaceProbe)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var volumes = new List<FileVolumeInfo>();

        void Add(string mountPoint, string kind)
        {
            if (!seen.Add(mountPoint))
                return;

            var (total, free) = spaceProbe?.Invoke(mountPoint) ?? (0, 0);
            volumes.Add(new FileVolumeInfo
            {
                Id = mountPoint,
                Label = MountLabel(mountPoint),
                Path = mountPoint,
                TotalBytes = total,
                FreeBytes = free,
                Kind = kind,
            });
        }

        // The root filesystem is always present.
        Add("/", "root");

        if (string.IsNullOrEmpty(procMountsContent))
            return volumes;

        foreach (var rawLine in procMountsContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // Fields: device mountpoint fstype options dump pass.
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3)
                continue;

            var device = Unescape(fields[0]);
            var mountPoint = Unescape(fields[1]);
            var fsType = fields[2];

            if (string.IsNullOrEmpty(mountPoint) || mountPoint == "/")
                continue;
            if (LinuxPseudoFileSystems.Contains(fsType))
                continue;
            if (IsRestrictedLinuxMount(mountPoint))
                continue;

            Add(mountPoint, ClassifyLinuxMount(device, fsType, mountPoint));
        }

        return volumes;
    }

    /// <summary>True when a Linux mount point is on (or under) the unconditional system denylist.</summary>
    private static bool IsRestrictedLinuxMount(string mountPoint)
    {
        foreach (var restricted in RestrictedLinuxMountPrefixes)
        {
            if (mountPoint == restricted
                || mountPoint.StartsWith(restricted + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string ClassifyLinuxMount(string device, string fsType, string mountPoint)
    {
        if (LinuxNetworkFileSystems.Contains(fsType) || device.StartsWith("//", StringComparison.Ordinal))
            return "network";

        if (mountPoint.StartsWith("/media/", StringComparison.Ordinal)
            || mountPoint.StartsWith("/mnt/", StringComparison.Ordinal)
            || mountPoint.StartsWith("/run/media/", StringComparison.Ordinal))
        {
            return "removable";
        }

        return "fixed";
    }

    private static string MountLabel(string mountPoint)
    {
        if (mountPoint == "/")
            return "/";

        var trimmed = mountPoint.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        var leaf = idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
        return string.IsNullOrEmpty(leaf) ? mountPoint : leaf;
    }

    /// <summary>
    /// Decodes the octal escapes the kernel writes into <c>/proc/mounts</c> fields (e.g. <c>\040</c> for a
    /// space, <c>\134</c> for a backslash). Non-escaped input is returned unchanged.
    /// </summary>
    private static string Unescape(string field)
    {
        if (field.IndexOf('\\') < 0)
            return field;

        var sb = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\'
                && i + 3 < field.Length
                && IsOctalDigit(field[i + 1])
                && IsOctalDigit(field[i + 2])
                && IsOctalDigit(field[i + 3]))
            {
                var code = ((field[i + 1] - '0') * 64) + ((field[i + 2] - '0') * 8) + (field[i + 3] - '0');
                sb.Append((char)code);
                i += 3;
            }
            else
            {
                sb.Append(field[i]);
            }
        }
        return sb.ToString();
    }

    private static bool IsOctalDigit(char c) => c is >= '0' and <= '7';

    private static long SafeSize(Func<long> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static string MapWindowsKind(DriveType type) => type switch
    {
        DriveType.Fixed => "fixed",
        DriveType.Removable => "removable",
        DriveType.CDRom => "removable",
        DriveType.Network => "network",
        DriveType.Ram => "fixed",
        _ => "fixed",
    };
}
