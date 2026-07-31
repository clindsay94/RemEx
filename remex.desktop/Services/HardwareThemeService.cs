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

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
        if (enabled)
        {
            _timer.Start();
            _ = PollHardwareStateAsync();
        }
        else
        {
            _timer.Stop();
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
