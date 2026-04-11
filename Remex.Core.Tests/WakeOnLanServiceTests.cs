using Remex.Core.Services.Network;

namespace Remex.Core.Tests;

public class WakeOnLanServiceTests
{
    private readonly WakeOnLanService _service = new();

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    public async Task WakeAsync_Accepts_Valid_MAC_Formats(string mac)
    {
        // Should not throw for valid MACs — actual send may fail in test env
        // but the parsing/validation should succeed
        await _service.WakeAsync(mac);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA:BB")]
    [InlineData("not-a-mac")]
    [InlineData("GG:HH:II:JJ:KK:LL")]
    public async Task WakeAsync_Rejects_Invalid_MAC(string mac)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _service.WakeAsync(mac));
    }

    [Fact]
    public async Task WakeAsync_Rejects_Invalid_BroadcastIp()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.WakeAsync("AA:BB:CC:DD:EE:FF", "not-an-ip"));
    }

    [Fact]
    public async Task WakeAsync_Accepts_Valid_BroadcastIp()
    {
        // Should not throw for valid broadcast IP
        await _service.WakeAsync("AA:BB:CC:DD:EE:FF", "255.255.255.255");
    }
}
