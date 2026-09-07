using System;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// RemEx-rjnbo.1: <c>TryResolve</c> is the third copy of the same optional-resolution logic
/// <c>SettingsViewModel.ResolveHostService</c> already de-duplicated for its paired-device list,
/// renamer and revoker ("ONE RESOLVER, because the third copy was about to land" per that method's
/// own remarks) - the tray flyout's device-count badge is that third copy, landing here instead of
/// as a fourth verbatim duplicate. These tests are the ones a private per-view-model copy would
/// never get.
/// </summary>
public class EmbeddedHostServiceLocatorTests
{
    private interface IMarker
    {
    }

    private sealed class Marker : IMarker
    {
    }

    [Fact]
    public void ReturnsNullWhenNeitherContainerHasIt()
    {
        // App.Services has no public setter and nothing in this test process ever assigns it
        // (that only happens in real app startup), so it is null here exactly like a unit test
        // environment always sees it - this asserts the null-host half of "neither container".
        using var _ = new Scoped(host: null);

        EmbeddedHostServiceLocator.TryResolve<IMarker>().Should().BeNull();
    }

    [Fact]
    public void ResolvesFromTheEmbeddedHostContainer()
    {
        var marker = new Marker();
        using var _ = new Scoped(host: new SingleService(marker));

        EmbeddedHostServiceLocator.TryResolve<IMarker>().Should().BeSameAs(marker);
    }

    private sealed class SingleService(IMarker marker) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IMarker) ? marker : null;
    }

    /// <summary>
    /// Swaps the embedded-host container for the life of a test and restores it, the same shape as
    /// <c>PairedDeviceCardTests.ScopedPairedDeviceSource</c> and for the identical reason: the
    /// resolver reads a process-wide static, and parallel execution is disabled assembly-wide, so
    /// save/restore is enough to keep this from leaking into another test.
    /// </summary>
    private sealed class Scoped : IDisposable
    {
        private readonly IServiceProvider? _savedHost = App.EmbeddedHostServices;

        public Scoped(IServiceProvider? host) => App.EmbeddedHostServices = host;

        public void Dispose() => App.EmbeddedHostServices = _savedHost;
    }
}
