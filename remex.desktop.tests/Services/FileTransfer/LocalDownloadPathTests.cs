using System;
using System.IO;
using FluentAssertions;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services.FileTransfer;

/// <summary>
/// A folder download builds local paths from the REMOTE side's manifest, and since RemEx-xt0af the
/// remote side can be a phone. Nothing it names may land outside the folder the person picked.
/// </summary>
public sealed class LocalDownloadPathTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("remex-local-download-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("Camera/2026/photo.jpg")]
    [InlineData("/Camera/photo.jpg")]
    [InlineData("..hidden/file")]
    [InlineData("name with spaces.txt")]
    public void AnOrdinaryPath_ResolvesInsideTheChosenFolder(string remote)
    {
        LocalDownloadPath.TryResolve(_root.FullName, remote, out var full).Should().BeTrue();
        full.Should().StartWith(_root.FullName + Path.DirectorySeparatorChar);
    }

    public static TheoryData<string?> HostilePaths() => new()
    {
        null,
        "",
        "   ",
        "/",
        "..",
        "../escape.txt",
        "../../escape.txt",
        "Camera/../../escape.txt",
        "Camera/./photo.jpg",
        ".",
        "...",
        "Camera/.../photo.jpg",
        "Camera//photo.jpg",
        "Camera/ /photo.jpg",
        @"..\..\escape.txt",
        @"Camera\..\..\escape.txt",
        @"C:\Windows\escape.txt",
        "C:/Windows/escape.txt",
        "C:escape.txt",
        @"\\server\share\escape.txt",
        "Camera/nul\0byte.txt",
        "Camera/what?.txt",
        "Camera/<pipe>|.txt",
        "CON",
        "Camera/nul.txt",
        "Camera/COM1",
        "LPT9.log",
        new string('a', LocalDownloadPath.MaxSegmentLength + 1),
        "Camera/" + new string('b', LocalDownloadPath.MaxSegmentLength + 1) + ".jpg",
    };

    [Theory]
    [MemberData(nameof(HostilePaths))]
    public void AHostilePath_IsRefused(string? remote)
    {
        LocalDownloadPath.TryResolve(_root.FullName, remote, out var full).Should().BeFalse();
        full.Should().BeNull();
    }

    [Fact]
    public void ANameAtTheLengthLimit_IsStillAccepted()
    {
        var name = new string('a', LocalDownloadPath.MaxSegmentLength);
        LocalDownloadPath.ToSafeRelativePath(name).Should().Be(name);
    }

    [Fact]
    public void WhateverIsAccepted_NeverResolvesOutsideTheRoot()
    {
        // The containment check is the second line, not a duplicate of the first: every candidate that
        // gets past the per-name rules must still land under the root.
        var candidates = new[]
        {
            "a/b/c.txt", "a.b/c", "a./b", "a /b", "~/x", "%TEMP%/x", "$HOME/x", "a/b/../c",
        };

        foreach (var remote in candidates)
        {
            if (LocalDownloadPath.TryResolve(_root.FullName, remote, out var full))
                Path.GetFullPath(full!).Should().StartWith(_root.FullName + Path.DirectorySeparatorChar, remote);
        }
    }
}
