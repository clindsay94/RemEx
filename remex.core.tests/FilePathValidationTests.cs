using System;
using System.IO;
using Remex.Core.Validation;

namespace Remex.Core.Tests;

/// <summary>
/// Edge-case coverage for the centralized <see cref="FilePathValidation"/> path-security helper: root
/// escape via <c>..</c>, absolute/UNC neutralization, the Linux system denylist, and filename sanitization.
/// </summary>
public class FilePathValidationTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "remex-fpv-root"));

    [Fact]
    public void ResolveWithinRoot_EmptyRelative_ReturnsRootItself()
    {
        Assert.Equal(Root, FilePathValidation.ResolveWithinRoot(Root, null));
        Assert.Equal(Root, FilePathValidation.ResolveWithinRoot(Root, ""));
        Assert.Equal(Root, FilePathValidation.ResolveWithinRoot(Root, "/"));
        Assert.Equal(Root, FilePathValidation.ResolveWithinRoot(Root, "\\"));
    }

    [Fact]
    public void ResolveWithinRoot_ValidNestedPath_StaysInsideRoot()
    {
        var resolved = FilePathValidation.ResolveWithinRoot(Root, "sub/dir/file.txt");
        Assert.StartsWith(Root + Path.DirectorySeparatorChar, resolved);
        Assert.EndsWith("file.txt", resolved);
    }

    [Fact]
    public void ResolveWithinRoot_LeadingSlash_IsNeutralizedToRelative()
    {
        // A leading slash must NOT be treated as filesystem-absolute; it is trimmed to a relative path.
        var resolved = FilePathValidation.ResolveWithinRoot(Root, "/etc/passwd");
        Assert.StartsWith(Root, resolved);
    }

    [Fact]
    public void ResolveWithinRoot_DotDotTraversal_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => FilePathValidation.ResolveWithinRoot(Root, "../escape.txt"));
        Assert.Throws<UnauthorizedAccessException>(
            () => FilePathValidation.ResolveWithinRoot(Root, "sub/../../escape.txt"));
    }

    [Fact]
    public void TryResolveWithinRoot_DotDotTraversal_ReturnsFalseWithError()
    {
        Assert.False(FilePathValidation.TryResolveWithinRoot(Root, "../../etc", out var resolved, out var error));
        Assert.Equal(string.Empty, resolved);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolveWithinRoot_UncStyleInput_DoesNotEscapeRoot()
    {
        // A UNC-looking input has its leading separators trimmed, so it cannot reach a network share;
        // it is neutralized to a relative subpath that stays inside the root.
        Assert.True(FilePathValidation.TryResolveWithinRoot(Root, "\\\\server\\share\\file", out var resolved, out _));
        Assert.StartsWith(Root, resolved);
    }

    [Fact]
    public void ResolveWithinRoot_WindowsDriveAbsolute_Throws()
    {
        if (!OperatingSystem.IsWindows())
            return; // Drive-absolute semantics only apply on Windows.

        Assert.Throws<UnauthorizedAccessException>(
            () => FilePathValidation.ResolveWithinRoot(@"C:\RemexRoot", @"D:\Windows\System32"));
    }

    [Fact]
    public void IsRestrictedSystemPath_LinuxDenylist_IsBlocked()
    {
        if (!OperatingSystem.IsLinux())
            return; // The denylist is Linux-only by design.

        Assert.True(FilePathValidation.IsRestrictedSystemPath("/proc"));
        Assert.True(FilePathValidation.IsRestrictedSystemPath("/proc/1/status"));
        Assert.True(FilePathValidation.IsRestrictedSystemPath("/sys/kernel"));
        Assert.True(FilePathValidation.IsRestrictedSystemPath("/dev/sda"));
        Assert.True(FilePathValidation.IsRestrictedSystemPath("/run/lock"));
        Assert.True(FilePathValidation.IsRestrictedSystemPath("/boot/efi/EFI"));
        Assert.False(FilePathValidation.IsRestrictedSystemPath("/home/user/docs"));
        // A path that merely shares a prefix substring must NOT be blocked.
        Assert.False(FilePathValidation.IsRestrictedSystemPath("/procession/notes.txt"));
    }

    [Fact]
    public void IsRestrictedSystemPath_NonLinux_AlwaysFalse()
    {
        if (OperatingSystem.IsLinux())
            return;

        Assert.False(FilePathValidation.IsRestrictedSystemPath("/proc/1"));
    }

    [Fact]
    public void TryResolveWithinRoot_LinuxDenylistUnderRoot_IsBlocked()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Root "/" with relative "proc" resolves to /proc, which the denylist blocks unconditionally.
        Assert.False(FilePathValidation.TryResolveWithinRoot("/", "proc", out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("file.txt", true)]
    [InlineData("My Folder", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    public void IsValidFileName_CoversCommonCases(string name, bool expectedValid)
    {
        var valid = FilePathValidation.IsValidFileName(name, out var error);
        Assert.Equal(expectedValid, valid);
        if (!expectedValid)
            Assert.NotNull(error);
    }

    [Fact]
    public void IsValidFileName_NullName_IsInvalid()
    {
        Assert.False(FilePathValidation.IsValidFileName(null, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void IsValidFileName_NulCharacter_IsInvalid()
    {
        // '\0' is in Path.GetInvalidFileNameChars() on every platform.
        Assert.False(FilePathValidation.IsValidFileName("a\0b", out _));
    }
}
