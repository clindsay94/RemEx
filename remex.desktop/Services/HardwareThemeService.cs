using Avalonia.Media;
using Avalonia.Threading;

namespace Remex.Desktop.Services;

/// <summary>
/// Optional service that polls local hardware state (e.g. OpenRGB) to dynamically 
/// update the application's accent color.
/// </summary>
public class HardwareThemeService : IDisposable
{
    private readonly ThemeService _themeService;
    private readonly DispatcherTimer _timer;
    private bool _isEnabled;

    public HardwareThemeService(ThemeService themeService)
    {
        _themeService = themeService;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += (s, e) => _ = PollHardwareStateAsync();
    }

    /// <summary>
    /// Turns hardware polling on or off. Idempotent by design — see the remarks.
    /// </summary>
    /// <remarks>
    /// THE EARLY RETURN CLOSES A FEEDBACK LOOP (review HIGH, RemEx-w6c4s). Since the hardware
    /// accent began routing through <c>ApplyCustomizationCore</c>, a hardware apply raises
    /// <c>ThemeService.CustomizationApplied</c> — which the old two-brush version never did.
    /// <c>ShellViewModel</c> handles that event by calling this method with
    /// <c>settings.SyncWithHardware</c>, and the enable branch below fires an IMMEDIATE poll rather
    /// than waiting for the timer. So without this guard the cycle is
    /// poll → apply → event → SetEnabled(true) → poll, bounded only by how fast the hardware
    /// answers, not by the 5-second interval: the dispatcher saturates with full ~56-resource
    /// applies and a crossfade that restarts before it finishes. It presents as a sluggish,
    /// shimmering UI with nothing in the log.
    /// Not reachable while <see cref="PollHardwareStateAsync"/> is still a stub (RemEx-dbjfy), but
    /// this is the wiring that bead will complete, and the guard is one line.
    /// It also stops the redundant <c>_timer.Start()</c> that every palette change was already
    /// triggering.
    /// </remarks>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _isEnabled) return;

        _isEnabled = enabled;
        if (enabled)
        {
            _timer.Start();
            _ = PollHardwareStateAsync();
        }
        else
        {
            _timer.Stop();

            // Turning sync off must restore the user's own seed (RemEx-w6c4s), not leave whatever
            // colour the last poll injected painted over it. A no-op when nothing was overriding.
            _themeService.ClearHardwareAccent();
        }
    }

    private async Task PollHardwareStateAsync()
    {
        if (!_isEnabled) return;

        try
        {
            // Placeholder: In a real scenario, this would connect to OpenRGB SDK
            // or poll a local FanControl / L-Connect web provider.
            // For now, we simulate a hardware color profile detection.
            
            // Example hypothetical check for local OpenRGB API
            // using var client = new HttpClient();
            // var response = await client.GetStringAsync("http://localhost:6742/color");
            // if (Color.TryParse(response, out var hardwareColor)) 
            //    _themeService.ApplyHardwareAccent(hardwareColor);
        }
        catch
        {
            // Silent fail — hardware polling is optional and non-critical.
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
