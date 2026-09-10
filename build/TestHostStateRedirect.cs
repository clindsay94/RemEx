using System.Reflection;
using System.Runtime.CompilerServices;
using Remex.Core.Services;

namespace Remex.Testing;

/// <summary>
/// Points every RemEx host-state store at a per-run temp directory, for the whole of a test
/// assembly, before any test code runs.
///
/// <para>
/// WHY (RemEx-4u29). The stores this redirects are not scratch files. An entry in
/// <c>paired_clients.json</c> is a credential record and an entry in
/// <c>file_transfer_trust.json</c> with <c>fullBrowseGranted</c> is standing authorisation to browse
/// the PC's filesystem; <c>cert.pfx</c> is the host's TLS identity, and creating a new one unpairs
/// every pinned client. Before this file, tests that did not inject a path resolved the production
/// location and wrote there: seven fixture identities — attacker-phone, victim-phone,
/// bodyless-phone, probe-phone, volumes-phone, reconnect-name-client, integration-test-client-1 —
/// were found in the developer's own <c>C:\ProgramData\RemEx\paired_clients.json</c>, mixed in with
/// four genuine client ids, and a fixture's full-browse grant was found in the trust store. Six of
/// the seven remained by the time this landed: a later test run deleted <c>probe-phone</c> outright,
/// which is the same suite still rewriting the same live credential store. Beyond
/// the security of it, that makes the stores useless as evidence: after a test run you cannot tell a
/// real pairing from a fixture by inspection.
/// </para>
///
/// <para>
/// A MODULE INITIALIZER, AND ONE COPY COMPILED INTO EACH TEST ASSEMBLY. It has to run before the
/// first test — the runtime guarantees a module initializer runs before any other code in its
/// module, which an xUnit fixture cannot promise. It cannot live in a shared library either: a
/// referenced assembly's initializer only runs once something touches that assembly, so a test that
/// never did would still reach the real store. Hence a linked source file, wired in
/// <c>Directory.Build.props</c> for every <c>*.tests</c> project, so a test project added later is
/// covered without anyone remembering to opt in — forgetting was the original failure.
/// </para>
///
/// <para>
/// Directories are left behind on purpose. They are small, they sit under the OS temp directory that
/// gets cleaned anyway, and deleting them in a finalizer would race the tests that are still writing
/// to them. A leftover directory is also the only evidence available when a test run has to be
/// investigated after the fact.
/// </para>
/// </summary>
internal static class TestHostStateRedirect
{
    [ModuleInitializer]
    internal static void RedirectHostStateToTemporaryDirectory()
    {
        // Assembly-named and GUID-suffixed so two runs cannot share a directory and let one
        // assembly's fixtures satisfy another's assertions. Note the limit, because the name suggests
        // more than it delivers: when vstest reuses ONE testhost process across several assemblies,
        // the last initializer to run overwrites the key, so an assembly whose singletons were already
        // constructed keeps its own directory while later resolutions in it land in the newer one.
        // The security property is unaffected — both are under the temp root, and neither is the
        // machine-wide store — but this is not the per-assembly isolation guarantee it looks like.
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "remex.tests";
        var directory = Path.Combine(
            Path.GetTempPath(),
            "remex-test-host-state",
            $"{assemblyName}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        AppContext.SetData(RemexDataPaths.HostStateDirectoryOverrideKey, directory);
    }
}
