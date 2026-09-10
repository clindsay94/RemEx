using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Remex.Desktop;

[assembly: AvaloniaTestApplication(typeof(Remex.Desktop.Render.Tests.RenderTestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// Entry point for the headless render harness (RemEx-0e9eq). Builds the real
/// <see cref="Remex.Desktop.App"/> - not a stand-in - with the Skia backend on the headless
/// platform, so App.axaml's MaterialTheme and BaseDarkGlass resources actually load and frames
/// actually rasterise.
/// </summary>
/// <remarks>
/// Mirrors <c>remex.agent/Program.cs</c>'s <c>BuildAvaloniaApp</c> minus <c>UsePlatformDetect</c>,
/// which the headless platform replaces. <c>.UseHarfBuzz().WithInterFont()</c> is carried over
/// unchanged so text is shaped the way production shapes it. Measured 2026-09-09: dropping both
/// does NOT fail any test here - Latin ASCII (the machine name) still rasterises without HarfBuzz,
/// and the "at least two colours" bar cannot see shaping loss, which only degrades complex scripts.
/// A shaping-dependent assertion (Devanagari or Arabic sample text) is the follow-up if that
/// silent failure needs catching; the pin in Directory.Packages.props is what guards it today.
///
/// PerTest isolation, not PerAssembly: <see cref="Remex.Desktop.App"/> holds static state
/// (<c>Services</c>, <c>IsShuttingDown</c>) that a shared Application instance across tests would
/// leak between them.
/// </remarks>
public static class RenderTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Remex.Desktop.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = true,
                FrameBufferFormat = Avalonia.Platform.PixelFormat.Bgra8888,
            })
            .UseHarfBuzz()
            .WithInterFont();

    /// <summary>
    /// Arms <see cref="Remex.Desktop.App.SkipProductionStartup"/> before the test session builds
    /// its first <c>Application</c> instance. A module initializer is the only hook guaranteed to
    /// run before that - an xUnit fixture constructor runs too late, after
    /// <c>OnFrameworkInitializationCompleted</c> has already fired production startup once.
    /// </summary>
    [ModuleInitializer]
    internal static void ArmTestSeam()
    {
        Remex.Desktop.App.SkipProductionStartup = true;
    }
}
