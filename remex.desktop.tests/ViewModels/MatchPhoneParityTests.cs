using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Spec § 5, Acceptance 3: one snapshot from the phone reproduces the phone's role hexes on the PC, negative contrast included.
/// The tuple is pinned on BOTH ends — MatchPhoneCaptureTest.kt builds it through the phone's real ThemeSync.buildEnvelope
/// and asserts these exact field values — and the expected hexes are the committed vector row for that cell, which
/// ThemeParityTest has already shown to be the phone's own ColorScheme. (RemEx-4kv0g.13)
/// </summary>
public class MatchPhoneParityTests
{
    // Verbatim theme_sync payload for: Personalize -> Palette "Custom" #0061A4, style Fidelity, Dark, Contrast -0.5, dynamic colour off.
    private const string CapturedThemeSyncJson =
        """{"seed":"#0061A4","style":"fidelity","mode":"dark","contrast":-0.5,"dynamic":false,"sentAtUnixMs":1757700000000}""";

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private static PhoneThemeSnapshot Captured()
    {
        // The same envelope shape PingPongHandler receives; deserialised through the source-generated context, never reflection.
        var envelope = $$"""{"type":"theme_sync","protocolVersion":2,"themeSync":{{CapturedThemeSyncJson}}}""";
        var message = JsonSerializer.Deserialize(envelope, RemexJsonSerializerContext.Default.RemexMessage);
        message.Should().NotBeNull();
        message!.ThemeSync.Should().NotBeNull("the captured payload must deserialise the way the host does it");
        return message.ThemeSync!;
    }

    private static Dictionary<string, uint> VectorRow(string seed, string variant, bool dark, double contrast)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "remex.core.tests", "Fixtures", "mcu-vectors.json")));
        var roles = doc.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();
        var row = doc.RootElement.GetProperty("vectors").EnumerateArray().Single(v =>
            v.GetProperty("seed").GetString() == seed && v.GetProperty("variant").GetString() == variant
            && v.GetProperty("dark").GetBoolean() == dark && v.GetProperty("contrast").GetDouble() == contrast);
        var argb = row.GetProperty("argb").EnumerateArray().Select(a => Convert.ToUInt32(a.GetString()!.Substring(1), 16)).ToArray();
        return roles.Zip(argb).ToDictionary(p => p.First, p => p.Second);
    }

    private static uint Argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    [Fact]
    public void TheSnapshotMapsToFidelityDarkMinusHalf()
    {
        var (seedHex, variant, mode, contrast) = CustomizationViewModel.TryMapPhoneTheme(Captured());
        seedHex.Should().Be("#0061A4");
        variant.Should().Be(SchemeVariants.Fidelity);
        mode.Should().Be(ThemeModes.Dark);
        contrast.Should().Be(-0.5, "a negative contrast is the phone's reduced-contrast scheme, not something to floor");
    }

    [Fact]
    public void MatchPhoneReproducesThePhonesRoleHexesExactly()
    {
        var (seedHex, variant, _, contrast) = CustomizationViewModel.TryMapPhoneTheme(Captured());
        var palette = DynamicColorGenerator.Generate(Color.Parse(seedHex!), variant!, isDark: true, contrast!.Value);
        var phone = VectorRow("#0061A4", "fidelity", dark: true, contrast: -0.5);

        // The three the spec names for the eyes pass, then the rest of what the PC paints from.
        Argb(palette.Primary).Should().Be(phone["primary"]);
        Argb(palette.Surface).Should().Be(phone["surface"]);
        Argb(palette.Error).Should().Be(phone["error"]);
        Argb(palette.OnPrimary).Should().Be(phone["onPrimary"]);
        Argb(palette.PrimaryContainer).Should().Be(phone["primaryContainer"]);
        Argb(palette.OnSurface).Should().Be(phone["onSurface"]);
        Argb(palette.Secondary).Should().Be(phone["secondary"]);
        Argb(palette.Tertiary).Should().Be(phone["tertiary"]);
        Argb(palette.Outline).Should().Be(phone["outline"]);
        foreach (var role in phone.Keys) palette.Roles[role].Should().Be(phone[role], $"role {role}");
    }
}
