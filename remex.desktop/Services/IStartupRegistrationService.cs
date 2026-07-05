namespace Remex.Desktop.Services;

/// <summary>
/// Platform service that manages registering the client application for autostart/launch-at-login.
/// </summary>
public interface IStartupRegistrationService
{
    bool IsSupported { get; }
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
