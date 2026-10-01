using Moq;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the host advertises its machine name in capabilities on every connect (RemEx-odqj5).
/// </summary>
/// <remarks>
/// The name already travelled in the pairing response, but nothing on the phone read it, and phones
/// paired before that change never see a pairing response again. Capabilities are sent on every
/// connect, so this is the path that reaches existing users and stops the phone labelling an
/// un-nicknamed PC by its IP address.
/// </remarks>
public class HostCapabilitiesMachineNameTests
{
    [Fact]
    public void CapabilitiesCarryThisMachinesName()
    {
        var provider = new HostCapabilitiesProvider(
            new FakeScreenCaptureService(), Mock.Of<IInputSimulationService>(), () => "");

        var caps = provider.GetCurrent();

        Assert.False(string.IsNullOrWhiteSpace(caps.MachineName));
        Assert.Equal(Environment.MachineName, caps.MachineName);
    }
}
