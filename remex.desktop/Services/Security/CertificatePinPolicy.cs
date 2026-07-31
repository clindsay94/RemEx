using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Remex.Desktop.Services.Security;

/// <summary>
/// The certificate-pinning rule shared by both TLS channels this app opens to the host: the control
/// channel (<c>ConnectionViewModel</c>) and the Remote Desktop stream
/// (<c>Services.Network.RemoteDesktopService</c>).
/// </summary>
/// <remarks>
/// <para>
/// Both channels connect to the SAME host, so disagreeing about its certificate is its own kind of
/// defect — and the two copies did disagree once. Until RemEx-mlce the Remote Desktop copy computed
/// the hash, looked it up and returned <c>true</c> regardless, so one channel enforced pinning while
/// the channel next to it accepted anything. Having one rule in one place is what stops that
/// recurring (RemEx-xmgw).
/// </para>
/// <para>
/// Everything here is a pure function. A validation callback runs on the TLS handshake thread and
/// must not block on async I/O, so pins are snapshotted before the connect and passed in. Notably
/// the decision does NOT mutate pairing state: a predicate that silently updated
/// <c>_isPairedWithCurrentHost</c> is precisely how the two copies drifted apart, so callers own
/// that update — see <see cref="IsPairedHost"/>.
/// </para>
/// </remarks>
public static class CertificatePinPolicy
{
    /// <summary>
    /// Computes the Base64 SHA-256 hash of a certificate's SubjectPublicKeyInfo — the value the
    /// pin store keys on, and what the Android client pins at pairing time.
    /// </summary>
    /// <remarks>
    /// Hashing the SPKI rather than the certificate bytes is what lets the host re-issue a
    /// certificate for the same key pair without breaking every paired device.
    /// </remarks>
    public static string ComputeSpkiHash(X509Certificate certificate)
    {
        using var cert2 = new X509Certificate2(certificate);
        var spki = cert2.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(SHA256.HashData(spki));
    }

    /// <summary>
    /// The pinning decision, as a pure function of the presented hash and the captured policy.
    /// </summary>
    /// <param name="spkiHashBase64">SPKI hash presented by the peer, from <see cref="ComputeSpkiHash"/>.</param>
    /// <param name="pins">
    /// Snapshot of the pin store taken before the connect, or <see langword="null"/> if no snapshot
    /// was loaded. A missing store is NOT an empty store.
    /// </param>
    /// <param name="allowFirstTimeTrust">
    /// Whether trust-on-first-use is permitted for this connect attempt — loopback for the stream
    /// channel, an explicit operator opt-in for the control channel. Only consulted when the store
    /// is empty; it can never override an existing pin.
    /// </param>
    public static bool IsCertificateAcceptable(
        string spkiHashBase64,
        IReadOnlyDictionary<string, string>? pins,
        bool allowFirstTimeTrust)
    {
        // No snapshot means the caller never loaded one. A missing store is not an empty store, and
        // the safe answer to "I do not know" is no.
        if (pins is null) return false;

        // Pinned hosts exist: the presented cert must be one of them, or this is a MITM.
        if (pins.Count > 0) return pins.Values.Contains(spkiHashBase64);

        // Empty store: trusted only where a first-time pairing handshake is about to provide the
        // MITM protection the absent pin cannot.
        return allowFirstTimeTrust;
    }

    /// <summary>
    /// Whether an accepted certificate means the host is actually PAIRED, as opposed to merely
    /// trusted for the duration of a first-time pairing handshake.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="IsCertificateAcceptable"/> rather than folded into it.
    /// Accepting on an empty store is trust-on-first-use, which is explicitly NOT a pairing: the
    /// pin does not exist yet, and treating that connect as paired would let the trust-on-first-use
    /// window masquerade as a verified host.
    /// </remarks>
    public static bool IsPairedHost(bool accepted, IReadOnlyDictionary<string, string>? pins)
        => accepted && pins is { Count: > 0 };
}
