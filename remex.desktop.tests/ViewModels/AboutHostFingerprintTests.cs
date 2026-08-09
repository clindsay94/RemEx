using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Services.Security;
using Remex.Desktop.Services.Security;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins that the About page actually shows this PC's certificate fingerprint, in the form the phone
/// shows it (RemEx-n8xk).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-vnps gave the Android client a dialog that shows the pinned fingerprint next to the one
/// answering now and asks the user to decide whether the change is legitimate. There was nowhere on
/// the PC that displayed its own, so the check the dialog asks for could not be performed.
/// </para>
/// <para>
/// TESTED AT THE VIEW MODEL, not at the formatter. <see cref="SpkiFingerprintDisplay"/> having the
/// right output proves nothing about whether the About page calls it — a page that binds to the raw
/// base64 leaves every formatter test green.
/// </para>
/// <para>
/// AND AT THE VIEW SOURCE, because a view-model test has the same blind spot one level up (review).
/// Avalonia binding failures are silent, so a page that binds to NOTHING also leaves every test here
/// green. <see cref="TheAboutPageActuallyBindsTheFingerprint"/> is the one that catches the row being
/// dropped or renamed; it reads the axaml as text, the same way FocusRingCoverageTests does.
/// </para>
/// </remarks>
public class AboutHostFingerprintTests : IDisposable
{
    private readonly List<AboutViewModel> _created = [];

    /// <summary>
    /// <see cref="App.EmbeddedHostServices"/> as it was before this fixture ran.
    /// </summary>
    /// <remarks>
    /// SAVED AND RESTORED because <see cref="AboutViewModel"/> falls back to the static containers
    /// when no service is injected, so a container left installed here would silently change what a
    /// later test asserts (review). Parallel execution is disabled assembly-wide, so save/restore is
    /// enough.
    /// <para>
    /// <see cref="App.Services"/> is NOT reset here, and cannot be: its setter is private, and the
    /// one test that swaps it (ConnectionViewModelTests) reaches the backing field by reflection and
    /// restores it in its own Dispose. It is <c>null</c> in this assembly otherwise, which is what
    /// the fallback chain expects. Reaching for reflection here to defend against that test breaking
    /// its own contract would buy less than it costs.
    /// </para>
    /// </remarks>
    private readonly IServiceProvider? _savedEmbeddedHostServices = App.EmbeddedHostServices;

    /// <summary>
    /// AboutViewModel subscribes to the static LocalizationService in its constructor, so an
    /// undisposed one keeps receiving locale changes for the life of the test assembly.
    /// </summary>
    public void Dispose()
    {
        foreach (var about in _created) about.Dispose();
        App.EmbeddedHostServices = _savedEmbeddedHostServices;
        GC.SuppressFinalize(this);
    }

    private AboutViewModel About(ICertificateService? certificateService, ConnectionViewModel? connection = null)
    {
        // null shell: AboutViewModel only stores it, and nothing on this path reads it.
        var about = new AboutViewModel(connection ?? new ConnectionViewModel(), null!, certificateService);
        _created.Add(about);
        return about;
    }

    [Fact]
    public void ThePageShowsTheHostFingerprintGroupedTheWayThePhoneShowsIt()
    {
        // The exact string the Android dialog renders for this pin. If these two ever disagree, the
        // user is comparing values that differ for a certificate that never changed.
        var about = About(new StubCertificateService("n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="));

        about.HostFingerprint.Should().Be("n8Kq 2LxV 9dR4 tYbF");
    }

    [Fact]
    public void WithNoHostInThisProcessTheRowShowsTheMarkerRatherThanBlank()
    {
        // A blank row says "this PC has no certificate", which is a more alarming claim than the one
        // that is true — that RemEx cannot tell you right now.
        //
        // The host container is cleared rather than merely assumed empty: with no service injected
        // the view model falls back to it, so leaving whatever a previous test installed would make
        // this pass or fail for reasons that have nothing to do with About (review).
        App.EmbeddedHostServices = null;

        var about = About(certificateService: null);

        about.HostFingerprint.Should().Be(SpkiFingerprintDisplay.Unavailable);
    }

    [Fact]
    public void TheAboutPageActuallyBindsTheFingerprint()
    {
        // THE ONE THAT SURVIVES THE VIEW BEING EDITED. Every other test here would stay green if the
        // row were deleted from AboutView.axaml, because they all stop at the view model and
        // Avalonia reports a missing binding by rendering nothing at all (review).
        var axaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "AboutView.axaml"));

        axaml.Should().Contain(
            "{Binding HostFingerprint}",
            "the About page is the only place a user can look this value up, and a dropped binding "
            + "fails silently");
        axaml.Should().Contain(
            "About_HostFingerprint",
            "an unlabelled row of base64 does not tell anyone what they are comparing");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    [Fact]
    public void AServiceThatCannotYieldAFingerprintShowsTheMarkerInsteadOfThrowing()
    {
        // DEFENSIVE, AND SAID SO PLAINLY. GetSpkiSha256Base64 throws until the certificate is loaded,
        // but HostBootstrapper awaits that load BEFORE registering the service, so the desktop cannot
        // observe a resolvable service holding an unloaded certificate. The branch is kept because an
        // unhandled throw takes down the page the user was told to visit, and because a certificate
        // the host cannot READ arrives here too — but this test pins a guard, not a live scenario.
        // Named accordingly, so nobody reads it as evidence that the lazy-load race is real (review).
        var about = About(StubCertificateService.ThatThrows());

        about.HostFingerprint.Should().Be(SpkiFingerprintDisplay.Unavailable);
    }

    [Fact]
    public void AHostContainerThatArrivesLateIsPickedUpRatherThanCachedAsAbsent()
    {
        // THE ORDERING CASE THAT IS ACTUALLY REACHABLE (review). The embedded host is started inside
        // a try/catch and publishes its container afterwards, while ShellViewModel keeps ONE About
        // instance for the session. So resolving the service once in the constructor would pin
        // "(none)" on screen permanently for anyone who opened About first — on the one page a user
        // visits precisely because their phone told them to check this value.
        //
        // This is why the service is re-resolved on every read instead of cached. The previous shape
        // of this test moved a certificate from unloaded to loaded, which pins a state the real
        // service cannot be in; the container going from absent to present is the one that can.
        App.EmbeddedHostServices = null;

        var connection = new ConnectionViewModel();
        var about = About(certificateService: null, connection);
        about.HostFingerprint.Should().Be(SpkiFingerprintDisplay.Unavailable);

        App.EmbeddedHostServices = new SingleServiceProvider(
            new StubCertificateService("n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="));
        connection.IsConnected = true;

        about.HostFingerprint.Should().Be("n8Kq 2LxV 9dR4 tYbF");
    }

    /// <summary>The smallest container that answers one question, so the test needs no DI package.</summary>
    private sealed class SingleServiceProvider(ICertificateService certificateService) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(ICertificateService) ? certificateService : null;
    }

    private sealed class StubCertificateService : ICertificateService
    {
        private string? _spki;

        public StubCertificateService(string? spki) => _spki = spki;

        /// <summary>A service that cannot produce a fingerprint, so GetSpkiSha256Base64 throws.</summary>
        public static StubCertificateService ThatThrows() => new((string?)null);

        public string GetSpkiSha256Base64() =>
            _spki ?? throw new InvalidOperationException(
                "Certificate not yet loaded. Call GetOrCreateCertificateAsync first.");

        public Task<X509Certificate2> GetOrCreateCertificateAsync(CancellationToken ct) =>
            Task.FromException<X509Certificate2>(new NotSupportedException());

        public Task RegenerateAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
