using Remex.Core.Models;

namespace Remex.Core.Services.Home;

/// <summary>
/// The host's in-memory meeting point between the PC Home's pinned sensors and the phone
/// (RemEx-wqo7a.5). Lives in <c>Remex.Core</c> so the desktop UI can depend on the abstraction without
/// referencing <c>Remex.Agent</c>, the same reason <c>IPhoneThemeSnapshotStore</c> does.
/// </summary>
/// <remarks>
/// <para>
/// **THE PC IS THE SINGLE OWNER, AND THIS IS NOT WHERE THE LIST IS KEPT.** The durable list is
/// <c>DashboardProfile.PinnedSensorIds</c>, written by the desktop's own pin paths. This store holds
/// only the latest snapshot the desktop published, so a session that attaches later can be told it,
/// and it carries a phone's requests the other way without deciding them. The desktop applies a
/// request through the canvas card (<c>SetCardPinned</c>) and then publishes the result; a request it
/// cannot honour is answered by republishing the unchanged list.
/// </para>
/// <para>
/// Events may be raised on any thread. A desktop subscriber marshals to the UI thread itself.
/// </para>
/// </remarks>
public interface IHomePinnedSensorsStore
{
    /// <summary>
    /// The latest published list: empty lists and revision 0 until the desktop has published once.
    /// Never null.
    /// </summary>
    HomePinnedSensors Current { get; }

    /// <summary>Raised with the new <see cref="Current"/> every time a publish changes it.</summary>
    event Action<HomePinnedSensors>? Changed;

    /// <summary>
    /// Records the PC's pinned and pinnable names, normalized through
    /// <c>HomePinsValidation.NormalizeNames</c>, bumps the revision and raises <see cref="Changed"/>.
    /// </summary>
    /// <returns>
    /// False, and no event, when both normalized lists equal the current ones in order under ordinal
    /// comparison — a re-save that changed nothing is not news for the phone.
    /// </returns>
    bool PublishFromPc(IReadOnlyList<string> pinned, IReadOnlyList<string> pinnable);

    /// <summary>
    /// Raised for each validated phone request, in arrival order, with the paired client it came from.
    /// </summary>
    event Action<HomePinChange, string>? PhoneChangeRequested;

    /// <summary>
    /// Hands a phone's already-validated request to the desktop by raising
    /// <see cref="PhoneChangeRequested"/>. Changes nothing by itself.
    /// </summary>
    void RequestFromPhone(HomePinChange change, string clientId);
}
