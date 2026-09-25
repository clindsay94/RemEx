using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// P1-28: the last decoded Remote Desktop frame (full display resolution) stayed resident on
/// <see cref="RemoteDesktopViewModel"/> after a manual stop or an unexpected disconnect, released
/// only when the next successful stream start overwrote it.
/// </summary>
/// <remarks>
/// A SOURCE SCAN, for the same reason <c>WallpaperLoadRaceTests</c> uses one on
/// <c>ShellViewModel</c>: constructing a real decoded frame here needs an actual
/// <c>Avalonia.Media.Imaging.Bitmap</c>/<c>WriteableBitmap</c>, and this test assembly has no
/// <c>Avalonia.Headless</c> reference — even a bare <c>WriteableBitmap</c> throws
/// <c>Unable to locate 'Avalonia.Platform.IPlatformRenderInterface'</c> with no render backend
/// registered, confirmed by actually trying it. <c>ClearCurrentFrame</c>'s own behavior (dispose,
/// then null) is a two-line method with no branching worth a separate test; what needs pinning is
/// that BOTH exit paths (a manual stop and an unexpected disconnect) actually call it.
/// </remarks>
public sealed class RemoteDesktopFrameReleaseTests
{
    [Fact]
    public void StopStreamAsyncsFinallyReleasesTheLastDecodedFrame()
    {
        var body = ExtractMethod(ViewModelSource(), "StopStreamAsync");

        body.Should().Contain("ClearCurrentFrame();",
            "a manual stop must release the last decoded frame, not leave it resident until the " +
            "next successful stream start overwrites it");
    }

    [Fact]
    public void OnDisconnectedReleasesTheLastDecodedFrame()
    {
        var body = ExtractMethod(ViewModelSource(), "OnDisconnected");

        body.Should().Contain("ClearCurrentFrame();",
            "an unexpected disconnect (network drop, host closing) must release the last decoded " +
            "frame just as reliably as a manual stop does");
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape — update this test");
        return match.Value;
    }

    private static string ViewModelSource([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, "..", "..")),
            "remex.desktop", "ViewModels", "RemoteDesktopViewModel.cs"));
}
