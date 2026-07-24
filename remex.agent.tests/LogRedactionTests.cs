using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

/// <summary>
/// VULN-1 (RemEx-s032.1): client identifiers must be redacted before they reach any log sink. The
/// in-memory log buffer is a disclosure surface — a full paired clientId is a replayable identifier, so
/// logs keep only a short, non-reversible prefix. This locks the redaction contract in place.
/// </summary>
public sealed class LogRedactionTests
{
    [Fact]
    public void RedactClientId_LongId_KeepsOnlyEightCharPrefix()
    {
        // A representative 122-bit UUID clientId.
        const string clientId = "3f9a1b2c-4d5e-6f70-8192-a3b4c5d6e7f8";

        var redacted = LogRedaction.RedactClientId(clientId);

        Assert.Equal("3f9a1b2c…", redacted);
        Assert.DoesNotContain(clientId, redacted);
        // The suffix (the bulk of the identifier) must be gone.
        Assert.DoesNotContain("e7f8", redacted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RedactClientId_NullOrEmpty_ReturnsPlaceholder(string? clientId)
    {
        Assert.Equal("<empty>", LogRedaction.RedactClientId(clientId));
    }

    [Fact]
    public void RedactClientId_ShortId_ReturnedAsIs()
    {
        // Anything ≤ 8 chars is already too short to add ellipsis noise; returned unchanged.
        Assert.Equal("abc123", LogRedaction.RedactClientId("abc123"));
    }
}
