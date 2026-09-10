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
}
