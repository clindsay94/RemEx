using System.Collections.Generic;

namespace Remex.Desktop.Services;

/// <summary>
/// The live client sessions the embedded host is holding, as the desktop UI can see them.
/// </summary>
/// <remarks>
/// <para>
/// THIS INTERFACE EXISTS FOR A DEPENDENCY DIRECTION, NOT FOR ABSTRACTION (RemEx-0z7w). The one
/// implementation is <c>Remex.Agent.Services.ClientSessionRegistry</c>, and it needed no changes to
/// satisfy this — its <c>Snapshot()</c> already returned exactly this shape. But the desktop cannot
/// name that type: <c>remex.agent</c> ProjectReferences <c>remex.desktop</c>, so a reference back the
/// other way would be a cycle. Declaring the contract on this side and letting the registry implement
/// it is the only arrangement that compiles, and it happens to be the honest one — the UI depends on
/// "somewhere to read sessions from", not on the host's registry.
/// </para>
/// <para>
/// RESOLVED FROM <c>App.EmbeddedHostServices</c>, because the host registers it in its own container
/// and the two containers are separate. Resolve it on every read rather than caching one in a
/// constructor: the host publishes its container after it starts, and a view model built first would
/// otherwise cache null for the session — the mistake found in review of RemEx-n8xk.
/// </para>
/// <para>
/// <see cref="Snapshot"/> returns AUTHENTICATED sessions only, and deliberately does NOT filter
/// loopback. That rule — a session is a phone only if it is not loopback — is stated once in
/// <see cref="PhonePresence"/> with its own tests, because re-deriving it per call site is how three
/// status dots came to show the wrong thing (RemEx-porg).
/// </para>
/// </remarks>
public interface IClientSessionSource
{
    /// <summary>Every authenticated live session right now. Never null; empty when none.</summary>
    IReadOnlyList<ClientSession> Snapshot();
}
