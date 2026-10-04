using Remex.Desktop.Services.Launching;

namespace Remex.Agent.Services.Launching;

/// <summary>
/// The non-Windows <see cref="IUnelevatedLauncher"/>: always answers
/// <see cref="UnelevatedLaunchResult.NotNeeded"/>, so <see cref="UserLauncher.Launch"/> takes the
/// standard launch and Linux behaves exactly as it did before RemEx-pp4cm.2. There is no split
/// administrator token to drop there.
/// </summary>
public sealed class NoOpUnelevatedLauncher : IUnelevatedLauncher
{
    /// <inheritdoc />
    public UnelevatedLaunchResult TryLaunch(string target, string? arguments, string? workingDirectory)
        => UnelevatedLaunchResult.NotNeeded;
}
