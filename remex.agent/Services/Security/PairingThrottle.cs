using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Per-IP sliding-window throttle for the pairing handshake paths
/// (<c>/start-pairing</c> and the <c>/ws</c> <c>pairing_complete</c> message).
/// </summary>
/// <remarks>
/// Pairing is the one place where an unauthenticated remote peer can drive host-side
/// cryptographic state, so it is the natural target for online brute-force / abuse. A short
/// session lifetime and a per-session failed-attempt cap (see <c>PairingService</c>) bound a
/// single session; this throttle bounds how fast a single source address can <em>start or
/// complete</em> pairing attempts across sessions. Registered as a singleton.
///
/// The window is a simple fixed-count sliding window: each source IP may make at most
/// <see cref="MaxAttempts"/> attempts within <see cref="Window"/>. Older timestamps age out.
/// A small cryptographically-derived jitter is added to the retry-after hint so a caller
/// cannot infer the exact window boundary and march in lock-step with it.
/// </remarks>
public sealed class PairingThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private const int MaxAttempts = 10;

    private readonly ConcurrentDictionary<string, AttemptHistory> _attempts =
        new(StringComparer.Ordinal);

    private readonly Func<DateTimeOffset> _now;

    public PairingThrottle()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    // Time source is injectable so the sliding-window behaviour can be tested without sleeping.
    internal PairingThrottle(Func<DateTimeOffset> now)
    {
        _now = now;
    }

    /// <summary>
    /// Records an attempt from <paramref name="remoteIp"/> and reports whether it is permitted.
    /// A null/loopback address is never throttled (in-process / local callers). When throttled,
    /// <paramref name="retryAfter"/> carries a jittered hint for how long the caller should wait.
    /// </summary>
    public bool TryRegisterAttempt(System.Net.IPAddress? remoteIp, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;

        // Loopback / in-process callers are trusted: the local host UI legitimately drives these
        // paths and must not be rate-limited. Non-loopback is the only abuse surface that matters.
        if (remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp))
        {
            return true;
        }

        var key = remoteIp.ToString();
        var now = _now();
        var cutoff = now - Window;

        var history = _attempts.GetOrAdd(key, static _ => new AttemptHistory());
        lock (history.Gate)
        {
            history.Prune(cutoff);

            if (history.Count >= MaxAttempts)
            {
                var oldest = history.Oldest;
                var baseWait = oldest + Window - now;
                if (baseWait < TimeSpan.Zero)
                {
                    baseWait = TimeSpan.Zero;
                }

                // Jitter ∈ [0, 1000ms), derived from a CSPRNG so the exact window edge is opaque.
                var jitterMs = RandomNumberGenerator.GetInt32(0, 1000);
                retryAfter = baseWait + TimeSpan.FromMilliseconds(jitterMs);
                return false;
            }

            history.Add(now);
            return true;
        }
    }

    private sealed class AttemptHistory
    {
        public readonly object Gate = new();

        // Bounded ring of recent attempt timestamps; capacity equals the per-window cap so a
        // throttled source can never grow this unboundedly.
        private readonly DateTimeOffset[] _stamps = new DateTimeOffset[MaxAttempts];
        private int _start;
        public int Count { get; private set; }

        public DateTimeOffset Oldest => _stamps[_start];

        public void Add(DateTimeOffset stamp)
        {
            var index = (_start + Count) % MaxAttempts;
            if (Count == MaxAttempts)
            {
                // Full: overwrite the oldest and advance the start pointer.
                _stamps[_start] = stamp;
                _start = (_start + 1) % MaxAttempts;
            }
            else
            {
                _stamps[index] = stamp;
                Count++;
            }
        }

        public void Prune(DateTimeOffset cutoff)
        {
            while (Count > 0 && _stamps[_start] < cutoff)
            {
                _start = (_start + 1) % MaxAttempts;
                Count--;
            }
        }
    }
}
