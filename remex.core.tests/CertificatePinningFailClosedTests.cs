using Remex.Core.Native;

namespace Remex.Core.Tests;

/// <summary>
/// RemEx-s032.5 / VULN-5: the JNI trust-manager overrides in <c>AndroidNativeExports</c> unconditionally
/// force the Android OS trust manager to accept ANY certificate — that is only safe because the
/// SPKI-pinning <c>RemoteCertificateValidationCallback</c> is assumed to always be authoritative. These
/// tests assert the fail-closed guard that keeps that assumption true: connecting with a missing/empty
/// SPKI pin must never be allowed to fall through to a state where no pinning validation runs.
/// </summary>
public class CertificatePinningFailClosedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RemexNativeClient_EnsurePinnedOrThrow_Rejects_Missing_Pin(string? spkiHash)
    {
        Assert.Throws<InvalidOperationException>(() => RemexNativeClient.EnsurePinnedOrThrow(spkiHash));
    }

    [Fact]
    public void RemexNativeClient_EnsurePinnedOrThrow_Accepts_Nonempty_Pin()
    {
        // Whitespace is rejected above (treated as "no real pin"); a genuine non-empty hash must pass.
        var exception = Record.Exception(() => RemexNativeClient.EnsurePinnedOrThrow("abc123=="));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RemexDesktopClient_EnsurePinnedOrThrow_Rejects_Missing_Pin(string? spkiHash)
    {
        Assert.Throws<InvalidOperationException>(() => RemexDesktopClient.EnsurePinnedOrThrow(spkiHash));
    }

    [Fact]
    public void RemexDesktopClient_EnsurePinnedOrThrow_Accepts_Nonempty_Pin()
    {
        var exception = Record.Exception(() => RemexDesktopClient.EnsurePinnedOrThrow("abc123=="));
        Assert.Null(exception);
    }

    [Fact]
    public async Task RemexNativeClient_ConnectAsync_Throws_When_Pin_Missing()
    {
        // Guards the real connect path, not just the extracted guard: an empty pin must never reach
        // ClientWebSocket.ConnectAsync (i.e. never reach a TLS handshake with no pinning callback wired).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemexNativeClient.Current.ConnectAsync("127.0.0.1", 5005, spkiHash: null, clientId: "test-client"));
    }

    [Fact]
    public async Task RemexDesktopClient_ConnectAsync_Throws_When_Pin_Missing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemexDesktopClient.Current.ConnectAsync("127.0.0.1", 5005, clientId: "test-client", spkiHash: null));
    }
}
