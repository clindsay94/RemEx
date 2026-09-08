using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins <see cref="LastSeedSidecar"/> (RemEx-alwfa.1, decision (b)): the round trip through
/// <c>last-seed.json</c>, and that the write is atomic (staged sibling, no debris) the same way
/// every other host-state store in this repo is. <see cref="RemexDataPaths.PerUserDirectory"/> is
/// already redirected assembly-wide by <c>build/TestHostStateRedirect.cs</c> before any test runs
/// (see <c>HostStateRedirectionTests</c>), so <see cref="LastSeedSidecar.FilePath"/> here never
/// touches the developer's real per-user directory. Each test deletes the sidecar (and any stray
/// staging sibling) before and after it runs so tests never see each other's file.
/// </summary>
public sealed class LastSeedSidecarTests : IDisposable
{
    public LastSeedSidecarTests() => CleanUp();

    public void Dispose() => CleanUp();

    private static void CleanUp()
    {
        var path = LastSeedSidecar.FilePath;
        if (File.Exists(path)) File.Delete(path);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return;
        foreach (var stray in Directory.EnumerateFiles(directory, $".{Path.GetFileName(path)}.*.tmp"))
            File.Delete(stray);
    }

    private static CustomizationSettings SampleSettings => new()
    {
        AccentColor = "#6C4CFF",
        SchemeVariant = SchemeVariants.Vibrant,
        ThemeMode = ThemeModes.Dark,
        ThemeContrast = 0.25,
    };

    [Fact]
    public async Task WriteAsync_ThenTryRead_RoundTripsSeedVariantModeAndContrast()
    {
        await LastSeedSidecar.WriteAsync(SampleSettings);

        LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
        seed.Seed.Should().Be("#6C4CFF");
        seed.Variant.Should().Be(SchemeVariants.Vibrant);
        seed.Mode.Should().Be(ThemeModes.Dark);
        seed.Contrast.Should().Be(0.25);
    }

    [Fact]
    public async Task WriteAsync_IsAtomic_NoStagingFileSurvives()
    {
        await LastSeedSidecar.WriteAsync(SampleSettings);

        File.Exists(LastSeedSidecar.FilePath).Should().BeTrue();
        var directory = Path.GetDirectoryName(LastSeedSidecar.FilePath)!;
        Directory.EnumerateFiles(directory, $".{Path.GetFileName(LastSeedSidecar.FilePath)}.*.tmp")
            .Should().BeEmpty("a completed write must leave no staging sibling behind");
    }

    [Fact]
    public void TryRead_WithNoSidecarFile_ReturnsFalse()
    {
        LastSeedSidecar.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void TryRead_WithCorruptJson_ReturnsFalse()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastSeedSidecar.FilePath)!);
        File.WriteAllText(LastSeedSidecar.FilePath, "{ this is not valid json");

        LastSeedSidecar.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_ThenWriteAsyncAgain_ReplacesTheContents()
    {
        await LastSeedSidecar.WriteAsync(SampleSettings);
        await LastSeedSidecar.WriteAsync(SampleSettings with { AccentColor = "#FF0000" });

        LastSeedSidecar.TryRead(out var seed).Should().BeTrue();
        seed.Seed.Should().Be("#FF0000");
    }
}
