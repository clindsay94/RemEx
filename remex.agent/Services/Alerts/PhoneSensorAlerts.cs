using Remex.Agent.Services.Security;
using Remex.Core.Guards;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Alerts;
using Remex.Core.Validation;

namespace Remex.Agent.Services.Alerts;

/// <summary>
/// The host's live <see cref="IPhoneSensorAlerts"/> (RemEx-pp4cm.12). One instance per running agent,
/// registered as a singleton so <c>PingPongHandler</c> (phone side) and the desktop's
/// <c>PhoneSensorAlertsBridge</c> (PC side, via <c>App.EmbeddedHostServices</c>) share it.
/// </summary>
/// <remarks>
/// <para>
/// **THE PUSH GATE.** A message goes to a client only when ALL of these hold: the session is open and
/// identity-proven (<see cref="ClientSessionRegistry.ProvenOpenClientIds"/>, which excludes loopback and
/// anything that merely named a phone), AND that client is still paired
/// (<see cref="PairedClientRegistry.IsClientPaired"/>, checked again at send time because a phone
/// unpaired a moment ago may still hold a session). Dropping either test sends a PC's alert text, sensor
/// names and thresholds to a connection that never proved it is one of the owner's phones.
/// </para>
/// <para>
/// HOLDS NO RULES AND NO QUEUE. A phone that is not connected simply is not told: alerts arrive while
/// the phone is connected, and the phone fetches the rules afresh each time it connects.
/// </para>
/// </remarks>
public sealed class PhoneSensorAlerts : IPhoneSensorAlerts
{
    private readonly ClientSessionRegistry _sessions;
    private readonly PairedClientRegistry _paired;
    private readonly ILogger<PhoneSensorAlerts> _logger;
    private readonly TimeProvider _time;
    private long _revision;

    public PhoneSensorAlerts(
        ClientSessionRegistry sessions,
        PairedClientRegistry paired,
        ILogger<PhoneSensorAlerts> logger,
        TimeProvider? timeProvider = null)
    {
        _sessions = Guard.NotNull(sessions);
        _paired = Guard.NotNull(paired);
        _logger = Guard.NotNull(logger);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event Action<PhoneSensorAlertRequest>? PhoneRequested;

    /// <inheritdoc />
    public Task PublishFiredAsync(SensorAlertFiredEvent fired)
    {
        if (fired is null
            || !HomePinsValidation.IsValidSensorName(fired.SensorName)
            || !double.IsFinite(fired.Value)
            || !double.IsFinite(fired.Threshold)
            || !SensorAlertValidation.IsValidDirection(fired.Direction)
            || !SensorAlertValidation.IsValidSeverity(fired.Severity))
        {
            // A reading that cannot be written as JSON would throw inside the send; refusing it here
            // keeps one bad value from ever reaching a socket.
            _logger.LogWarning("Not sending a sensor alert to phones: the alert carries a value that cannot travel.");
            return Task.CompletedTask;
        }

        return BroadcastAsync(new RemexMessage { Type = MessageTypes.SensorAlertFired, SensorAlertFired = fired });
    }

    /// <inheritdoc />
    public Task PublishRulesAsync(IReadOnlyList<SensorAlertRule> rules)
    {
        var snapshot = new SensorAlertRules
        {
            Rules = SensorAlertValidation.NormalizeRules(rules),
            Revision = Interlocked.Increment(ref _revision),
            UpdatedUtc = _time.GetUtcNow(),
        };

        return BroadcastAsync(new RemexMessage { Type = MessageTypes.SensorAlertRules, SensorAlertRules = snapshot });
    }

    /// <inheritdoc />
    public void RequestFromPhone(PhoneSensorAlertRequest request)
    {
        // The handler validates before calling; this is the belt to that brace, so a future caller that
        // forgets cannot hand the desktop a blank name, a non-finite threshold or an unknown enum.
        var wellFormed = request is not null
            && request.Kind switch
            {
                PhoneSensorAlertRequestKind.Get => true,
                PhoneSensorAlertRequestKind.Set => SensorAlertValidation.IsWellFormedChange(request.Change),
                PhoneSensorAlertRequestKind.Remove => HomePinsValidation.IsValidSensorName(request.SensorName),
                _ => false,
            };
        if (!wellFormed)
        {
            return;
        }

        PhoneRequested?.Invoke(request!);
    }

    /// <summary>Sends <paramref name="message"/> to every client that passes the push gate.</summary>
    private async Task BroadcastAsync(RemexMessage message)
    {
        foreach (var clientId in _sessions.ProvenOpenClientIds())
        {
            if (!_paired.IsClientPaired(clientId))
            {
                continue;
            }

            try
            {
                await _sessions.TrySendAsync(clientId, message, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // TrySendAsync reports an unreachable phone as false; anything thrown is a fault in
                // building the message. Neither may stop the next phone from being told.
                _logger.LogWarning(ex, "Sending {Type} to a phone failed.", message.Type);
            }
        }
    }
}
