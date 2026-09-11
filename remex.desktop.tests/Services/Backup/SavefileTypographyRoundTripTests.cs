using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Models.Backup;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services.Backup;

/// <summary>
/// RemEx-jt6w5.1, shaped after <see cref="SavefileSensorAlertsRoundTripTests"/> (RemEx-8wpvr.7): a
/// manual export and re-import must carry <see cref="CustomizationSettings.Typography"/> into a FRESH
/// install unchanged. The export side is free (the whole <see cref="DashboardProfile"/> is serialized);
/// the guard is the import → SaveAsync → ReloadAsync chain that replaces CurrentProfile.
/// </summary>
public sealed class SavefileTypographyRoundTripTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<DashboardLayoutService> _layoutServices = new();

    private static readonly TypographySettings Tuned = new()
    {
        HeadersScale = 1.35, BodyScale = 0.9, SmallScale = 1.1, SensorScale = 1.5,
        HeadersBold = true, SensorBold = true, SensorTitleBackdrop = true,
        ShadowEnabled = false, ShadowStrength = 85,
    };

    private string CreateTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("remex-jt6w5-1-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var service in _layoutServices) service.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private DashboardLayoutService CreateLayoutService()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var service = new DashboardLayoutService(Path.Combine(CreateTempDir(), "dashboard_layout.json"), theme);
        _layoutServices.Add(service);
        return service;
    }

    private RemexSavefileService CreateSavefileService(DashboardLayoutService layoutService) => new(
        layoutService,
        new LauncherStorageService(CreateTempDir()),
        new FileTransferRootSettingsService(),
        new FakeDashboardProfileStorageService());

    [Fact]
    public async Task ExportThenImport_RoundTripsTypography_IntoAFreshDashboardProfile()
    {
        var sourceLayout = CreateLayoutService();
        await sourceLayout.LoadAsync();
        var sourceProfile = sourceLayout.CurrentProfile with
        {
            Customization = sourceLayout.CurrentProfile.Customization with { Typography = Tuned, CornerRadius = 9 },
        };
        await sourceLayout.SaveAsync(sourceProfile);
        await sourceLayout.ReloadAsync();

        using var stream = new MemoryStream();
        await CreateSavefileService(sourceLayout).ExportAsync(stream);
        stream.Position = 0;

        // FRESH doubles for import: own temp layout file, own launcher directory, own host fake.
        var destLayout = CreateLayoutService();
        await destLayout.LoadAsync();
        var result = await CreateSavefileService(destLayout).ImportAsync(stream);

        result.AppliedSections.Should().Contain(nameof(RemexSavefileSections.DashboardLayout));
        result.Warnings.Should().BeEmpty();
        destLayout.CurrentProfile.Customization.Typography.Should().Be(Tuned,
            "the savefile carries the whole DashboardProfile, typography included, and the import must write it back unchanged");
        destLayout.CurrentProfile.Customization.CornerRadius.Should().Be(9,
            "the rest of Customization must ride along untouched");
    }

    [Fact]
    public void SavefileEnvelope_KeepsTypographyKeys_AtSerializerLevel()
    {
        var savefile = new RemexSavefile
        {
            FormatVersion = RemexSavefile.CurrentFormatVersion,
            CreatedAtUtc = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc),
            AppVersion = "2.5.0",
            Os = "windows",
            Kind = "manual",
            Sections = new RemexSavefileSections
            {
                DashboardLayout = new DashboardProfile { Customization = new CustomizationSettings { Typography = Tuned } },
            },
        };

        var json = JsonSerializer.Serialize(savefile, RemexSavefileService.JsonOptions);
        json.Should().Contain("\"typography\"").And.Contain("shadowStrength");

        var back = JsonSerializer.Deserialize<RemexSavefile>(json, RemexSavefileService.JsonOptions);
        back!.Sections.DashboardLayout!.Customization.Typography.Should().Be(Tuned);
    }

    private sealed class FakeDashboardProfileStorageService : IDashboardProfileStorageService
    {
        public Task<DashboardProfile> LoadProfileAsync() => Task.FromResult(new DashboardProfile());
        public Task SaveProfileAsync(DashboardProfile profile) => Task.CompletedTask;
    }
}
