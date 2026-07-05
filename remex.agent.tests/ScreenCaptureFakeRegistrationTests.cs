using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Services;
using Remex.Core.Services.Command;
using Remex.Agent.Services.Session;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Guards the test-harness safety net from RemEx-21g: integration-test hosts must NEVER resolve the
/// OS-disrupting host services, because booting them wedges the GPU/DWM (real DXGI Desktop Duplication)
/// or physically locks the developer's Windows session (the real session guard runs <c>tscon</c>).
/// Resolving (not engaging) the services here is safe — none lock/capture from their constructor.
/// </summary>
public sealed class ScreenCaptureFakeRegistrationTests : IClassFixture<RemexHostFactory>
{
    private readonly RemexHostFactory _factory;

    public ScreenCaptureFakeRegistrationTests(RemexHostFactory factory) => _factory = factory;

    [Fact]
    public void DefaultHost_ResolvesSafeDoubles_NotRealOsDisruptingServices()
    {
        // Forces the host to build (lazy) and resolves the same singletons the bootstrapper would.
        _ = _factory.Services;

        Assert.IsType<FakeScreenCaptureService>(_factory.Services.GetRequiredService<IScreenCaptureService>());
        Assert.IsType<NoOpInteractiveSessionGuard>(_factory.Services.GetRequiredService<IInteractiveSessionGuard>());
        Assert.IsType<NoOpSystemCommandService>(_factory.Services.GetRequiredService<ISystemCommandService>());
    }
}
