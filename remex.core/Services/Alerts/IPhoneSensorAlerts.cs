using Remex.Core.Models;

namespace Remex.Core.Services.Alerts;

/// <summary>What a phone asked the PC to do with its sensor alert rules.</summary>
public enum PhoneSensorAlertRequestKind
{
    /// <summary>Send me the rule list.</summary>
    Get,

    /// <summary>Add this rule, or replace the sensor's existing one.</summary>
    Set,

    /// <summary>Remove the sensor's rule.</summary>
    Remove,
}

/// <summary>
/// One validated request from a paired phone, handed to the desktop (RemEx-pp4cm.12).
/// </summary>
/// <param name="Kind">What was asked.</param>
/// <param name="ClientId">The paired client it came from, as the connection proved it.</param>
/// <param name="Change">The rule to add or replace, for <see cref="PhoneSensorAlertRequestKind.Set"/>.</param>
/// <param name="SensorName">The sensor to remove, for <see cref="PhoneSensorAlertRequestKind.Remove"/>.</param>
public sealed record PhoneSensorAlertRequest(
    PhoneSensorAlertRequestKind Kind,
    string ClientId,
    SensorAlertChange? Change = null,
    string? SensorName = null);

/// <summary>
/// The host's meeting point between the PC's sensor alerts and its paired phones (RemEx-pp4cm.12).
/// Lives in <c>Remex.Core</c> so the desktop UI can depend on the abstraction without referencing
/// <c>Remex.Agent</c>, the same reason <c>IHomePinnedSensorsStore</c> does.
/// </summary>
/// <remarks>
/// <para>
/// **THE PC IS THE SINGLE OWNER, AND THIS HOLDS NO RULES.** The durable list is
/// <c>DashboardProfile.SensorAlerts</c>, kept by <c>SensorAlertStore</c> and fired by the desktop's one
/// <c>SensorAlertTracker</c>. This only carries the PC's output to phones (<see cref="PublishFiredAsync"/>,
/// <see cref="PublishRulesAsync"/>) and a phone's requests the other way, without deciding them: the
/// desktop applies a request on its UI thread and answers by publishing the rules, accepted or refused.
/// </para>
/// <para>
/// Both publishers reach ONLY paired phones with a live, identity-proven, non-loopback session, and
/// never throw: a phone that walked away is not a fault. Events may be raised on any thread; a
/// desktop subscriber marshals to the UI thread itself.
/// </para>
/// </remarks>
public interface IPhoneSensorAlerts
{
    /// <summary>
    /// Tells every reachable paired phone a rule fired. Called only when the PC's own tracker decided
    /// to notify, so the tracker's cooldown is honoured here and no second evaluator exists.
    /// </summary>
    Task PublishFiredAsync(SensorAlertFiredEvent fired);

    /// <summary>
    /// Sends every reachable paired phone the PC's whole rule list, normalized through
    /// <c>SensorAlertValidation.NormalizeRules</c>, with a revision that only ever goes up.
    /// </summary>
    Task PublishRulesAsync(IReadOnlyList<SensorAlertRule> rules);

    /// <summary>Raised for each validated phone request, in arrival order.</summary>
    event Action<PhoneSensorAlertRequest>? PhoneRequested;

    /// <summary>
    /// Hands a phone's already-validated request to the desktop by raising <see cref="PhoneRequested"/>.
    /// Changes nothing by itself.
    /// </summary>
    void RequestFromPhone(PhoneSensorAlertRequest request);
}
