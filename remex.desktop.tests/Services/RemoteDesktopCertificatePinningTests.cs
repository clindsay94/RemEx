using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Desktop.Services.Network;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The Remote Desktop channel's certificate pinning must actually decide something.
/// </summary>
/// <remarks>
/// <c>ValidateServerCertificate</c> computed the presented SPKI hash, looked it up in
/// <c>PinnedCertStore</c>, and then returned <c>true</c> regardless — the lookup had no effect and
/// a comment reading "Fallback for RemoteDesktop connecting" sat where the rejection should have
/// been. Every certificate was accepted on the H.264/MJPEG channel while the control channel next
/// to it enforced pinning properly (RemEx-mlce).
/// <para>
/// The rule is deliberately identical to <c>ConnectionViewModel.AcceptSelfSignedCertificate</c>.
/// Both channels connect to the same host, so disagreeing about its certificate would be its own
/// defect — and matching the already-hardened sibling is what makes this change safe rather than
/// novel.
/// </para>
/// </remarks>
public class RemoteDesktopCertificatePinningTests
{
    private const string Presented = "presented-spki-hash";

    private static IReadOnlyDictionary<string, string> Pins(params string[] hashes)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i < hashes.Length; i++)
            d[$"host-{i}"] = hashes[i];
        return d;
    }

    /// <summary>
    /// A missing snapshot means the preparation step never ran. A missing store is not an empty
    /// store, and the safe answer to "I do not know" is no.
    /// </summary>
    [Fact]
    public void NoSnapshot_IsRejected_EvenOnLoopback()
    {
        RemoteDesktopService.IsCertificateAcceptable(Presented, null, allowFirstTimeTrust: true)
            .Should().BeFalse();
    }

    [Fact]
    public void PinnedHash_IsAccepted()
    {
        RemoteDesktopService.IsCertificateAcceptable(
            Presented, Pins("other-host-hash", Presented), allowFirstTimeTrust: false)
            .Should().BeTrue();
    }

    /// <summary>
    /// The case the old code got wrong: pins exist, the cert matches none of them, and it was
    /// accepted anyway.
    /// </summary>
    [Fact]
    public void UnpinnedHash_IsRejected_WhenPinsExist()
    {
        RemoteDesktopService.IsCertificateAcceptable(
            Presented, Pins("some-other-hash"), allowFirstTimeTrust: false)
            .Should().BeFalse();
    }

    /// <summary>
    /// Loopback does NOT override an existing pin set. Being more permissive than the control
    /// channel would mean the two disagreed about the same host's certificate.
    /// </summary>
    [Fact]
    public void Loopback_DoesNotOverrideAnExistingPinSet()
    {
        RemoteDesktopService.IsCertificateAcceptable(
            Presented, Pins("some-other-hash"), allowFirstTimeTrust: true)
            .Should().BeFalse();
    }

    /// <summary>
    /// An empty store on loopback is the ordinary case and must keep working.
    /// </summary>
    /// <remarks>
    /// This is why making the check effective does not break the PC's own session:
    /// <c>SetPinAsync</c> is only ever called inside ConnectionViewModel's pairing block, and
    /// loopback sets <c>_isPairedWithCurrentHost</c> true BEFORE that block — so a PC that only
    /// talks to its own embedded host never pins anything and its store stays empty.
    /// </remarks>
    [Fact]
    public void EmptyStore_OnLoopback_IsAccepted()
    {
        RemoteDesktopService.IsCertificateAcceptable(Presented, Pins(), allowFirstTimeTrust: true)
            .Should().BeTrue();
    }

    [Fact]
    public void EmptyStore_OffLoopback_IsRejected()
    {
        RemoteDesktopService.IsCertificateAcceptable(Presented, Pins(), allowFirstTimeTrust: false)
            .Should().BeFalse();
    }

    /// <summary>
    /// The decision must be RETURNED, not merely computed.
    /// </summary>
    /// <remarks>
    /// Every assertion above would still pass against the original code, which called the lookup
    /// and then returned <c>true</c> anyway. That is the entire defect, so the call site is checked
    /// too — and the old <c>.GetAwaiter().GetResult()</c> must not come back, since a TLS callback
    /// runs on the handshake thread and must not block on async I/O.
    /// </remarks>
    [Fact]
    public void ValidationCallback_ReturnsTheDecision_AndDoesNotBlockOnAsyncIo()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Services", "Network", "RemoteDesktopService.cs"));

        source.Should().Contain("var accepted = IsCertificateAcceptable(hashBase64, _pinSnapshot, _allowFirstTimeTrust);",
            "the callback must consult the rule");
        source.Should().Contain("return accepted;",
            "and return its answer rather than an unconditional true");
        source.Should().NotContain("GetAllPinsAsync().GetAwaiter().GetResult()",
            "pins are snapshotted before connect; blocking on async I/O inside a TLS handshake " +
            "callback is what PrepareTlsValidationAsync exists to avoid");
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
