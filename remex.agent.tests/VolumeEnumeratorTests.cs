using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;

namespace Remex.Agent.Tests;

/// <summary>
/// WP2 coverage for <see cref="VolumeEnumerator"/>'s Linux <c>/proc/mounts</c> parser (plan §1.2 / §2):
/// root always present, real mounts surfaced, the unconditional system denylist and pseudo-filesystem
/// filter applied, octal-escape decoding, kind classification, dedup, and space probing. The parser is a
/// pure static so these run identically on any host OS (this repo builds/tests on Windows and Linux).
/// </summary>
public sealed class VolumeEnumeratorTests
{
    // A representative /proc/mounts: pseudo filesystems, the real root, denied system paths, fixed data
    // mounts, a removable USB with an octal-escaped space, and a CIFS network share.
    private const string SampleMounts =
        "sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0\n" +
        "proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0\n" +
        "udev /dev devtmpfs rw,nosuid,relatime 0 0\n" +
        "tmpfs /run tmpfs rw,nosuid,nodev,noexec,relatime 0 0\n" +
        "/dev/sda2 / ext4 rw,relatime 0 0\n" +
        "/dev/sda1 /boot/efi vfat rw,relatime 0 0\n" +
        "/dev/sda3 /home ext4 rw,relatime 0 0\n" +
        "/dev/sdb1 /mnt/data ext4 rw,relatime 0 0\n" +
        "/dev/sdc1 /media/connor/USB\\040Stick vfat rw,relatime 0 0\n" +
        "//nas/share /mnt/net cifs rw,relatime 0 0\n" +
        "tmpfs /dev/shm tmpfs rw,nosuid,nodev 0 0\n";

    [Fact]
    public void ParseLinuxMounts_IncludesRootAndRealMounts()
    {
        var paths = VolumeEnumerator.ParseLinuxMounts(SampleMounts, null)
            .Select(v => v.Path).ToHashSet();

        Assert.Contains("/", paths);
        Assert.Contains("/home", paths);
        Assert.Contains("/mnt/data", paths);
        Assert.Contains("/mnt/net", paths);
        Assert.Contains("/media/connor/USB Stick", paths);
    }

    [Fact]
    public void ParseLinuxMounts_ExcludesRestrictedAndPseudoMounts()
    {
        var paths = VolumeEnumerator.ParseLinuxMounts(SampleMounts, null)
            .Select(v => v.Path).ToHashSet();

        // Denylist (plan §2) — enforced unconditionally.
        Assert.DoesNotContain("/proc", paths);
        Assert.DoesNotContain("/sys", paths);
        Assert.DoesNotContain("/dev", paths);
        Assert.DoesNotContain("/run", paths);
        Assert.DoesNotContain("/boot/efi", paths);
        // Under a denied prefix.
        Assert.DoesNotContain("/dev/shm", paths);
    }

    [Fact]
    public void ParseLinuxMounts_ClassifiesVolumeKinds()
    {
        var vols = VolumeEnumerator.ParseLinuxMounts(SampleMounts, null);

        Assert.Equal("root", vols.Single(v => v.Path == "/").Kind);
        Assert.Equal("fixed", vols.Single(v => v.Path == "/home").Kind);
        Assert.Equal("network", vols.Single(v => v.Path == "/mnt/net").Kind);
        Assert.Equal("removable", vols.Single(v => v.Path == "/media/connor/USB Stick").Kind);
    }

    [Fact]
    public void ParseLinuxMounts_DecodesOctalEscapeInLabel_AndDefaultsSizesToZero()
    {
        var usb = VolumeEnumerator.ParseLinuxMounts(SampleMounts, null)
            .Single(v => v.Path == "/media/connor/USB Stick");

        Assert.Equal("USB Stick", usb.Label);
        Assert.Equal(0L, usb.TotalBytes);
        Assert.Equal(0L, usb.FreeBytes);
    }

    [Fact]
    public void ParseLinuxMounts_DeduplicatesMountPoints()
    {
        var content = SampleMounts + "/dev/sda3 /home ext4 rw,relatime 0 0\n";
        var vols = VolumeEnumerator.ParseLinuxMounts(content, null);

        Assert.Single(vols, v => v.Path == "/home");
    }

    [Fact]
    public void ParseLinuxMounts_UsesSpaceProbeWhenSupplied()
    {
        var vols = VolumeEnumerator.ParseLinuxMounts(SampleMounts, _ => (100L, 40L));
        var home = vols.Single(v => v.Path == "/home");

        Assert.Equal(100L, home.TotalBytes);
        Assert.Equal(40L, home.FreeBytes);
    }

    [Fact]
    public void ParseLinuxMounts_EmptyContent_ReturnsRootOnly()
    {
        var vols = VolumeEnumerator.ParseLinuxMounts(string.Empty, null);

        var only = Assert.Single(vols);
        Assert.Equal("/", only.Path);
        Assert.Equal("root", only.Kind);
    }
}
