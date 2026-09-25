using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Perf audit P2-18: <c>UpgradeLowResolutionIcons</c> used to re-run GDI+/shell extraction on every
/// launch for a target that had already proven it has nothing sharper, forever. <c>AppEntry.IconUpgradeUnfixable</c>
/// (additive field, no protocolVersion bump - RG:396-401) records that verdict so the same futile
/// work is skipped on every future load.
/// </summary>
public sealed class LauncherIconUpgradeUnfixableTests
{
    private sealed class FakeLauncherStorage : ILauncherStorageService
    {
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>());
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }

    private sealed class FakeIconService : IIconExtractionService
    {
        public int CallCount { get; private set; }
        public Func<string, string?> Extract { get; set; } = _ => null;

        public string ExtractIconAsBase64(string filePath)
        {
            CallCount++;
            return Extract(filePath) ?? string.Empty;
        }
    }

    /// <summary>A trivially low-resolution "icon" - NeedsSharperIcon(null) is true, which is the cheapest way to force the refresh path.</summary>
    private const string? LowResIcon = null;

    private static AppEntry Entry(string targetPath, bool unfixable = false) =>
        new(Guid.NewGuid(), "App", targetPath, "#4A3AFF", LowResIcon, IconUpgradeUnfixable: unfixable);

    private static (AppLauncherViewModel Vm, FakeIconService IconService) MakeVm() =>
        MakeVmWithExtract(_ => null);

    private static (AppLauncherViewModel Vm, FakeIconService IconService) MakeVmWithExtract(Func<string, string?> extract)
    {
        var iconService = new FakeIconService { Extract = extract };
        var vm = new AppLauncherViewModel(new ConnectionViewModel(), null!, new FakeLauncherStorage(), iconService: iconService);
        return (vm, iconService);
    }

    [Fact]
    public void AnEntryAlreadyMarkedUnfixable_IsSkippedWithoutCallingTheExtractor()
    {
        var (vm, iconService) = MakeVm();
        var entries = new List<AppEntry> { Entry(GetType().Assembly.Location, unfixable: true) };

        var result = vm.UpgradeLowResolutionIcons(entries, out var changed).ToList();

        iconService.CallCount.Should().Be(0, "an entry already proven unfixable must not be re-extracted");
        changed.Should().BeFalse();
        result[0].IconUpgradeUnfixable.Should().BeTrue();
    }

    [Fact]
    public void AnExtractionThatStillYieldsNothingSharper_MarksTheEntryUnfixable()
    {
        // Returns the SAME low-res value every time - the extractor genuinely has nothing better.
        var (vm, iconService) = MakeVmWithExtract(_ => "");
        var entries = new List<AppEntry> { Entry(GetType().Assembly.Location) };

        var result = vm.UpgradeLowResolutionIcons(entries, out var changed).ToList();

        iconService.CallCount.Should().Be(1, "the first attempt must still run - only a SUBSEQUENT load should skip it");
        changed.Should().BeTrue("the unfixable flag itself is new state worth persisting");
        result[0].IconUpgradeUnfixable.Should().BeTrue();
        result[0].IconBase64.Should().Be(LowResIcon, "a failed upgrade must not overwrite the icon that's already there");
    }

    [Fact]
    public void AThrowingExtractor_DoesNotMarkTheEntryUnfixable()
    {
        // A transient failure (e.g. a locked file) is not the same claim as "this target has
        // nothing sharper" - it deserves a real retry next launch, not a permanent give-up.
        var (vm, iconService) = MakeVmWithExtract(_ => throw new InvalidOperationException("locked"));
        var entries = new List<AppEntry> { Entry(GetType().Assembly.Location) };

        var result = vm.UpgradeLowResolutionIcons(entries, out var changed).ToList();

        changed.Should().BeFalse();
        result[0].IconUpgradeUnfixable.Should().BeFalse(
            "an exception must not be treated as proof the target has nothing sharper");
    }

    /// <summary>Builds a base64 PNG whose IHDR reports a full-size, real-artwork-density image - the shape <c>LauncherIconResolutionTests.PngOfDensity</c> uses for "this needs no further upgrade".</summary>
    private static string SharpFakePng()
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        System.Text.Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 256);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), 256);
        Array.Resize(ref bytes, (int)(256 * 256 * 0.053)); // real-artwork density, per LauncherIconResolutionTests
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public void AGenuineImprovement_UpdatesTheIconAndDoesNotMarkUnfixable()
    {
        var sharperIcon = SharpFakePng();
        var (vm, iconService) = MakeVmWithExtract(_ => sharperIcon);
        var entries = new List<AppEntry> { Entry(GetType().Assembly.Location) };

        var result = vm.UpgradeLowResolutionIcons(entries, out var changed).ToList();

        iconService.CallCount.Should().Be(1);
        changed.Should().BeTrue();
        result[0].IconBase64.Should().Be(sharperIcon);
        result[0].IconUpgradeUnfixable.Should().BeFalse("a genuine improvement is not the same as giving up");
    }

    [Fact]
    public void TheRealExtractorsFallbackPlaceholder_IsTreatedAsTransientNotUnfixable()
    {
        // Review round 1 MEDIUM: DesktopIconExtractionService (the real Windows implementation)
        // never actually throws - every internal failure already degrades to this exact known
        // placeholder. Without this check that placeholder read as "confirmed no sharper icon",
        // permanently flagging a target that merely had a locked file today.
        var (vm, iconService) = MakeVmWithExtract(_ => IconExtractionService.FallbackBase64Icon);
        var entries = new List<AppEntry> { Entry(GetType().Assembly.Location) };

        var result = vm.UpgradeLowResolutionIcons(entries, out var changed).ToList();

        iconService.CallCount.Should().Be(1);
        changed.Should().BeFalse();
        result[0].IconUpgradeUnfixable.Should().BeFalse(
            "the fallback placeholder means the extraction attempt failed, not that it succeeded and found nothing sharper");
    }

    /// <summary>
    /// Review round 1 HIGH, THE ACTUAL REGRESSION: <c>NormalizeEntry</c> rebuilds a fresh
    /// <c>AppEntry</c> from individual fields on every load, and the first cut of this fix left
    /// <c>IconUpgradeUnfixable</c> off that constructor call - silently resetting it to false every
    /// single time, which made the whole fix a no-op and added an extraction + a save on every
    /// launch instead of removing them. Goes through the REAL <c>LoadLaunchersAsync</c> path (via
    /// the constructor), not <c>UpgradeLowResolutionIcons</c> directly, because that is exactly the
    /// seam the first four tests in this file could not see the bug through.
    /// </summary>
    [Fact]
    public void AnUnfixableFlagSurvivesNormalizationOnLoad_AndTheExtractorIsNeverCalled()
    {
        var iconService = new FakeIconService { Extract = _ => throw new InvalidOperationException("must not be called") };
        var storage = new StorageWithEntries(new List<AppEntry> { Entry(GetType().Assembly.Location, unfixable: true) });

        var vm = new AppLauncherViewModel(new ConnectionViewModel(), null!, storage, iconService: iconService);

        vm.Launchers.Should().ContainSingle(e => e.IconUpgradeUnfixable);
        iconService.CallCount.Should().Be(0,
            "NormalizeEntry must preserve IconUpgradeUnfixable, or every load re-discovers and re-persists the same verdict");
    }

    private sealed class StorageWithEntries : ILauncherStorageService
    {
        private readonly List<AppEntry> _entries;
        public StorageWithEntries(List<AppEntry> entries) => _entries = entries;
        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(_entries);
        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries) => Task.CompletedTask;
    }
}
