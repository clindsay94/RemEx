using Moq;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Routines;
using Remex.Core.Native;
using Remex.Core.Routines;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Both ends advertise Routines (routines spec §7.5, RemEx-pp0rt.3).
/// </summary>
/// <remarks>
/// The phone gates every routine feature on <c>HostCapabilities.supportsRoutines</c> (an older PC
/// gets <c>pc_too_old</c>, never the raw command verb), and the host must never send a
/// <c>routine_*</c> message to a client that did not advertise <c>ClientCapabilities.supportsRoutines</c>.
/// </remarks>
public class RoutineCapabilityAdvertisementTests
{
    private static HostCapabilitiesProvider Provider() =>
        new(new FakeScreenCaptureService(), Mock.Of<IInputSimulationService>(), () => "0A:1B:2C:3D:4E:5F");

    [Fact]
    public void TheHostAdvertisesRoutinesAndItsSchemaVersion()
    {
        var capabilities = Provider().GetCurrent();

        Assert.True(capabilities.SupportsRoutines);
        Assert.Equal(RoutineSchema.CurrentVersion, capabilities.RoutineSchemaVersion);
        Assert.NotNull(capabilities.RoutinePowerVerbs);
    }

    [Fact]
    public void TheAdvertisedVerbsAreRoutineVerbsAndNeverWakeOnLan()
    {
        var verbs = Provider().GetCurrent().RoutinePowerVerbs!;

        Assert.DoesNotContain(RoutinePowerVerbs.ReservedWakeOnLan, verbs);
        Assert.All(verbs, v => Assert.Contains(v, RoutinePowerVerbs.All));

        // Windows only. On Linux the probe advertises a verb only when logind says yes (Shutdown,
        // Lock) or an X11 session with xset exists (MonitorOff) - RemEx-pp0rt.9 - so a host without
        // logind or X11 legitimately offers none of them; MapLinux's own tests pin that mapping.
        if (OperatingSystem.IsWindows())
        {
            Assert.NotEmpty(verbs);

            // These need no platform capability and are always offered.
            Assert.Contains(RoutinePowerVerbs.Lock, verbs);
            Assert.Contains(RoutinePowerVerbs.Shutdown, verbs);
            Assert.Contains(RoutinePowerVerbs.MonitorOff, verbs);
        }
    }

    [Fact]
    public void TheProbeNeverThrowsAndReturnsASubsetInOrder()
    {
        var verbs = RoutinePowerVerbProbe.Probe();

        var expectedOrder = RoutinePowerVerbs.All.Where(verbs.Contains).ToList();
        Assert.Equal(expectedOrder, verbs);
    }

    [Fact]
    public void ThePhoneNativeClientAdvertisesRoutines()
    {
        var capabilities = RemexNativeClient.BuildCapabilities;

        Assert.True(capabilities.SupportsRoutines);
        Assert.Equal(RoutineSchema.CurrentVersion, capabilities.RoutineSchemaVersion);
        Assert.True(capabilities.SupportsConsentPrompt);
    }
}
