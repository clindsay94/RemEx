using Remex.Core.Messages;

namespace Remex.Core.Services;

/// <summary>
/// In-process access to the host's telemetry samples, for UI that runs inside the host process.
/// </summary>
/// <remarks>
/// <para>
/// Exists so the PC's own dashboard does not read its own telemetry back out of a TLS socket. The
/// embedded UI auto-connects to <c>wss://localhost:5005/ws</c>, so its data path was serialize →
/// encrypt → loopback adapter → decrypt → rebuild the whole record graph, once a second, forever, to
/// deliver a value that was already sitting in this process (RemEx-ite8).
/// </para>
/// <para>
/// Declared here for consistency rather than necessity: <c>remex.agent</c> references
/// <c>remex.desktop</c>, so the UI project could equally have declared this and the host implemented
/// it. Core is where every other service <c>EmbeddedHostServiceLocator</c> resolves already lives
/// (<c>ILauncherStorageService</c> and friends), and following that beats saving one interface from
/// the Android link.
/// </para>
/// <para>
/// ONLY VALID FOR A LOOPBACK CONNECTION. A UI pointed at another machine must keep taking telemetry
/// off the socket — this reports the sample for the machine it is running on, which would silently be
/// the wrong computer's readings.
/// </para>
/// </remarks>
public interface ITelemetryBroadcaster
{
    /// <summary>The most recent sample, or <see langword="null"/> before the first poll completes.</summary>
    TelemetryPayload? CurrentTelemetry { get; }

    /// <summary>
    /// Raised on the sampling thread each time a new sample is published. Subscribers must marshal to
    /// their own thread; a UI subscriber posts to the dispatcher.
    /// </summary>
    event Action<TelemetryPayload>? TelemetryPublished;

    /// <summary>
    /// Declares that something is reading these samples right now, and keeps the sampler running
    /// until the returned lease is disposed (perf audit P0-10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE SAMPLER ONLY SAMPLES WHILE SOMEONE HOLDS ONE. It used to poll every sensor once a second for
    /// the whole life of the process, so a tray-resident PC with no phone attached and its window
    /// hidden was paying for ~450 sensor reads a second that nothing looked at. Subscribing to
    /// <see cref="TelemetryPublished"/> is NOT a claim of demand: the in-process UI stays subscribed
    /// for its whole life, and whether it actually needs samples changes as windows show and hide.
    /// </para>
    /// <para>
    /// Disposing the lease is idempotent. Holding several is fine: the sampler runs while ANY is held.
    /// While nothing is held, <see cref="CurrentTelemetry"/> may go back to null rather than keep
    /// reporting a reading that is no longer current; the next sample lands within one period of a
    /// lease being taken.
    /// </para>
    /// </remarks>
    IDisposable AcquireDemand();
}
