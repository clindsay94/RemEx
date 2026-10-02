using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-qs5aa: the launcher's icon upgrade runs off the UI thread. If a host sync or an offline drop
/// changes <c>Launchers</c> meanwhile, the upgrade's result must be merged into that newer list, not
/// replace it with the stale local load (and then be saved over the newer state).
/// </summary>
public sealed class LauncherLoadRaceTests
{
    private sealed class StoredLauncher(List<AppEntry> stored) : ILauncherStorageService
    {
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public List<AppEntry>? LastSaved { get; private set; }

        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(stored.ToList());

        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries)
        {
            LastSaved = entries.ToList();
            Interlocked.Increment(ref _saves);
            return Task.CompletedTask;
        }
    }

    /// <summary>Blocks every extraction until released, holding the race window open.</summary>
    private sealed class GatedIconService(string icon) : IIconExtractionService
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public string ExtractIconAsBase64(string filePath)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            return icon;
        }
    }

    private static string SharpFakePng()
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        System.Text.Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 256);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), 256);
        Array.Resize(ref bytes, (int)(256 * 256 * 0.053));
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public async Task ALauncherChangeDuringTheIconUpgradeIsKeptAndTheUpgradeIsMergedIntoIt()
    {
        var target = GetType().Assembly.Location;
        var stale = new AppEntry(Guid.NewGuid(), "App", target, "#4A3AFF", null);
        var storage = new StoredLauncher([stale]);
        var sharp = SharpFakePng();
        var icons = new GatedIconService(sharp);

        var vm = new AppLauncherViewModel(new ConnectionViewModel(), null!, storage, iconService: icons);
        icons.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the upgrade should be running");

        // A host sync lands while the extraction is still running: the same app, renamed, plus a new one.
        var added = new AppEntry(Guid.NewGuid(), "Added", target, "#112233", sharp);
        vm.Launchers = new ObservableCollection<AppEntry>([stale with { DisplayName = "Renamed" }, added]);
        icons.Release.Set();

        for (var i = 0; i < 400 && storage.Saves == 0; i++)
        {
            await Task.Delay(10);
        }

        storage.Saves.Should().BeGreaterThan(0, "the upgraded icon is new state worth persisting");
        vm.Launchers.Select(e => e.DisplayName).Should().Equal("Renamed", "Added");
        vm.Launchers[0].IconBase64.Should().Be(sharp, "the upgrade still applies to the entry it was computed for");
        storage.LastSaved!.Select(e => e.DisplayName).Should().Equal("Renamed", "Added");
    }

    [Fact]
    public void AnEntryWhoseIconChangedMeanwhileKeepsTheNewerIcon()
    {
        var id = Guid.NewGuid();
        var loaded = new AppEntry(id, "App", "C:/a.exe", "#000000", null);
        var upgraded = loaded with { IconBase64 = "upgraded" };
        var current = loaded with { IconBase64 = "from-host" };

        var merged = AppLauncherViewModel.MergeUpgradedIcons([current], [loaded], [upgraded], out var changed);

        changed.Should().BeFalse();
        merged.Single().IconBase64.Should().Be("from-host");
    }

    [Fact]
    public void AnEntryRemovedMeanwhileStaysRemoved()
    {
        var loaded = new AppEntry(Guid.NewGuid(), "Gone", "C:/a.exe", "#000000", null);
        var other = new AppEntry(Guid.NewGuid(), "Other", "C:/b.exe", "#000000", null);

        var merged = AppLauncherViewModel.MergeUpgradedIcons(
            [other], [loaded], [loaded with { IconBase64 = "upgraded" }], out var changed);

        changed.Should().BeFalse();
        merged.Should().Equal(other);
    }
}
