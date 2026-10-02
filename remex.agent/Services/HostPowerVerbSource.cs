using Remex.Core.Guards;
using Remex.Desktop.Services;

namespace Remex.Agent.Services;

/// <summary>
/// Hands the PC Commands page the same power-verb list the phone gets in <c>host_info</c>
/// (<see cref="Remex.Core.Models.HostCapabilities.RoutinePowerVerbs"/>), so both apps hide the same
/// actions on a PC that cannot do them (RemEx-kq10x.3).
/// </summary>
public sealed class HostPowerVerbSource(IHostCapabilitiesProvider capabilities) : IHostPowerVerbSource
{
    private readonly IHostCapabilitiesProvider _capabilities = Guard.NotNull(capabilities);

    /// <inheritdoc />
    public IReadOnlyCollection<string>? GetPowerVerbs() => _capabilities.GetCurrent().RoutinePowerVerbs;
}
