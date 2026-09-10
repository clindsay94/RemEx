using Remex.Core.Services.Network;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins what the host promises a phone about its MAC address (RemEx-izuj).
/// </summary>
/// <remarks>
/// The selection itself cannot be asserted here — which adapter is "primary" depends on the machine
/// running the test, and an assertion about the build agent's NICs would pin the agent rather than
/// this code. What CAN be pinned is the contract the phone relies on: the shape when an address is
/// found, and that the absent case is empty rather than a placeholder.
/// </remarks>
public class PrimaryNetworkAdapterTests
{
    [Fact]
    public void TheFormatIsWhatSomeoneComparingAgainstARouterWouldExpect()
    {
        // Colon-separated and upper-case, chosen for the human reading it off a DHCP table — the
        // wire does not care, since WakeOnLanService strips separators before building the packet.
        Assert.Equal(
            "0A:1B:2C:3D:4E:5F",
            PrimaryNetworkAdapter.Format([0x0A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F]));
    }

    [Fact]
    public void EveryByteIsTwoDigitsSoAnAddressIsNeverShortened()
    {
        // The case a naive ToString("X") loses: a leading-zero byte must not collapse to one digit,
        // or the string is not a MAC and nothing downstream will match it.
        Assert.Equal("00:00:00:00:00:01", PrimaryNetworkAdapter.Format([0, 0, 0, 0, 0, 1]));
    }

    [Fact]
    public void FindEitherReturnsAWellFormedAddressOrNothingAtAll()
    {
        // THE CONTRACT THE PHONE DEPENDS ON. An empty string means "ask the user"; anything non-empty
        // is used to wake a machine. A half-formed or placeholder value would fail silently and give
        // no reason, which is why the production code returns empty rather than guessing.
        //
        // Machine-independent by construction: it asserts a disjunction, so it holds on an agent with
        // no NICs at all and on one with several.
        var (mac, adapter) = PrimaryNetworkAdapter.Find();

        if (mac.Length == 0)
        {
            Assert.Empty(adapter);
            return;
        }

        Assert.Equal(17, mac.Length);
        Assert.Equal(5, mac.Count(c => c == ':'));
        Assert.All(mac.Split(':'), part =>
        {
            Assert.Equal(2, part.Length);
            Assert.True(byte.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _));
        });
        Assert.NotEmpty(adapter);
    }
}
