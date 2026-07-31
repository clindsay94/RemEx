using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Services.Network;
using Remex.Desktop.Services.Security;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Certificate pinning must actually decide something, and both TLS channels must ask the same
/// question. Covers <c>CertificatePinPolicy</c> — the single rule the Remote Desktop stream and the
/// control channel now share (RemEx-xmgw) — as well as the call sites that must keep consulting it.
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
        CertificatePinPolicy.IsCertificateAcceptable(Presented, null, allowFirstTimeTrust: true)
            .Should().BeFalse();
    }

    [Fact]
    public void PinnedHash_IsAccepted()
    {
        CertificatePinPolicy.IsCertificateAcceptable(
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
        CertificatePinPolicy.IsCertificateAcceptable(
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
        CertificatePinPolicy.IsCertificateAcceptable(
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
        CertificatePinPolicy.IsCertificateAcceptable(Presented, Pins(), allowFirstTimeTrust: true)
            .Should().BeTrue();
    }

    [Fact]
    public void EmptyStore_OffLoopback_IsRejected()
    {
        CertificatePinPolicy.IsCertificateAcceptable(Presented, Pins(), allowFirstTimeTrust: false)
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

        // Whitespace-normalised so the assertion pins the CALL, not its line breaks. Matching the
        // raw source would make a re-wrap or a re-indent fail this test with a message about
        // certificate pinning, which is a confusing way to learn that a formatter ran.
        var normalized = Regex.Replace(source, @"\s+", " ");

        normalized.Should().Contain(
            "var accepted = CertificatePinPolicy.IsCertificateAcceptable(hashBase64, _pinSnapshot, _allowFirstTimeTrust);",
            "the callback must consult the rule, with the snapshot and policy it captured");
        source.Should().Contain("return accepted;",
            "and return its answer rather than an unconditional true");
        source.Should().NotContain("GetAllPinsAsync().GetAwaiter().GetResult()",
            "pins are snapshotted before connect; blocking on async I/O inside a TLS handshake " +
            "callback is what PrepareTlsValidationAsync exists to avoid");
    }

    /// <summary>
    /// Neither TLS channel may keep its own copy of the pinning rule.
    /// </summary>
    /// <remarks>
    /// This is the property RemEx-xmgw's dedupe actually buys. The unit tests above pin the rule's
    /// behaviour, but they are blind to a second implementation appearing beside it — which is
    /// exactly what happened: two validators that looked alike, one of which ignored its own lookup
    /// and accepted everything (RemEx-mlce). A copy is caught here by the lookup expression, which
    /// is the one line a re-implementation cannot avoid writing.
    /// </remarks>
    [Fact]
    public void NeitherChannel_ReimplementsThePinLookup()
    {
        foreach (var relativePath in new[]
                 {
                     Path.Combine("remex.desktop", "Services", "Network", "RemoteDesktopService.cs"),
                     Path.Combine("remex.desktop", "ViewModels", "ConnectionViewModel.cs"),
                 })
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

            source.Should().NotContain("pins.Values.Contains", because:
                $"{relativePath} must delegate to CertificatePinPolicy rather than keep a second " +
                "copy of the rule; the two copies disagreed for real once");
            source.Should().Contain("CertificatePinPolicy.IsCertificateAcceptable", because:
                $"{relativePath} still opens a TLS channel to the host and must consult the rule");
        }
    }

    /// <summary>
    /// Only a MATCHED PIN counts as paired — accepting on an empty store does not.
    /// </summary>
    /// <remarks>
    /// The control channel sets <c>_isPairedWithCurrentHost</c> from this, and it used to fall out
    /// of the branch structure: the old code assigned <c>true</c> in exactly one branch (matched
    /// pin) and <c>false</c> in all four others, including the branch that ACCEPTS under
    /// trust-on-first-use. Folding the rule into a shared predicate is only behaviour-preserving if
    /// that asymmetry survives, so it is asserted rather than assumed. Getting it wrong would mark
    /// the trust-on-first-use window as a verified pairing.
    /// </remarks>
    [Theory]
    [InlineData(true, 1, true)]    // accepted against a non-empty store => matched pin => paired
    [InlineData(true, 0, false)]   // accepted against an empty store => trust-on-first-use, NOT paired
    [InlineData(false, 1, false)]  // rejected against a non-empty store
    [InlineData(false, 0, false)]  // rejected, nothing to pair with
    public void IsPairedHost_IsTrueOnlyForAMatchedPin(bool accepted, int pinCount, bool expected)
    {
        var pins = pinCount == 0 ? Pins() : Pins(Presented);

        CertificatePinPolicy.IsPairedHost(accepted, pins).Should().Be(expected);
    }

    [Fact]
    public void IsPairedHost_IsFalse_WhenNoSnapshotWasLoaded()
    {
        // A missing snapshot cannot be a pairing, and the callback rejects in that case anyway.
        CertificatePinPolicy.IsPairedHost(accepted: true, pins: null).Should().BeFalse();
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
