using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Perf audit P3-52: a debounced save whose serialized content is byte-identical to the last write
/// skips the disk entirely. The atomic-write rules (REGRESSION-GUARDS "Profile writes to disk must be
/// atomic") are untouched; this only decides whether a write is needed at all.
/// </summary>
public class DashboardLayoutUnchangedSaveTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "remex-dashboard-layout-unchanged-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private DashboardLayoutService NewService() =>
        new(Path.Combine(_tempDirectory, "dashboard_layout.json"), new ThemeService());

    [Fact]
    public async Task ADebouncedSaveOfUnchangedContentDoesNotWrite()
    {
        using var service = NewService();
        var loaded = await service.LoadAsync();
        var saves = 0;
        service.ProfileSaved += () => saves++;

        service.RequestSave(loaded);
        await service.FlushAsync();
        saves.Should().Be(1);

        service.RequestSave(loaded with { });
        await service.FlushAsync();
        saves.Should().Be(1, "the same content is already on disk");

        service.RequestSave(loaded with { Customization = loaded.Customization with { CornerRadius = 3 } });
        await service.FlushAsync();
        saves.Should().Be(2, "changed content is written");
    }

    [Fact]
    public async Task ADirectSaveAlwaysWrites()
    {
        // SaveAsync is the import path; its caller reloads straight after and must never be handed a
        // skipped write it cannot tell apart from a real one.
        using var service = NewService();
        var loaded = await service.LoadAsync();
        var saves = 0;
        service.ProfileSaved += () => saves++;

        await service.SaveAsync(loaded);
        await service.SaveAsync(loaded);

        saves.Should().Be(2);
    }

    [Fact]
    public async Task AFileChangedBehindOurBackIsRewrittenEvenWithUnchangedContent()
    {
        // A savefile restore or another instance may have replaced the file since our last write; the
        // skip only holds while disk still carries exactly what this instance wrote.
        using var service = NewService();
        var loaded = await service.LoadAsync();
        var saves = 0;
        service.ProfileSaved += () => saves++;

        service.RequestSave(loaded);
        await service.FlushAsync();
        await File.WriteAllTextAsync(service.FilePathForTests, "{}");
        File.SetLastWriteTimeUtc(service.FilePathForTests, DateTime.UtcNow.AddMinutes(5));

        service.RequestSave(loaded);
        await service.FlushAsync();

        saves.Should().Be(2);
    }

    [Fact]
    public async Task ADeletedFileIsRewrittenEvenWithUnchangedContent()
    {
        using var service = NewService();
        var loaded = await service.LoadAsync();
        var saves = 0;
        service.ProfileSaved += () => saves++;

        service.RequestSave(loaded);
        await service.FlushAsync();
        File.Delete(service.FilePathForTests);

        service.RequestSave(loaded);
        await service.FlushAsync();

        saves.Should().Be(2);
        File.Exists(service.FilePathForTests).Should().BeTrue();
    }
}
