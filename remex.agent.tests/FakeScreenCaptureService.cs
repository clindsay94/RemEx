using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Agent.Tests;

/// <summary>
/// Headless <see cref="IScreenCaptureService"/> for integration tests. Pure managed code — it never
/// touches DXGI Desktop Duplication, Direct3D, or GDI screen access, so booting a host with this
/// registered cannot open a real Desktop Duplication session.
///
/// <para>
/// <see cref="RemexHostFactory"/> registers this by default for every host it builds. That matters
/// because the real <c>WindowsScreenCaptureService</c> opens a live <c>DuplicateOutput</c> session in
/// its constructor, and <c>HostCapabilitiesProvider</c> takes <see cref="IScreenCaptureService"/> as a
/// constructor dependency that the bootstrapper resolves eagerly at startup. Without this fake, every
/// test-class host boot would open + tear down a real duplication session; back-to-back across the
/// full suite that storm wedges the NVIDIA driver and DWM (black screen + cursor — see RemEx-crk /
/// RemEx-21g).
/// </para>
///
/// <para>
/// Tests that exercise capture-specific behaviour register their own mock last (last-wins), e.g. the
/// private <c>MockScreenCaptureService</c> in <c>RemoteDesktopHandlerTests</c>; this fake only protects
/// the fixtures that never override capture.
/// </para>
/// </summary>
public sealed class FakeScreenCaptureService : IScreenCaptureService
{
    // Minimal valid JPEG: SOI + APP0 + EOI markers. Matches the shape RemoteDesktopHandlerTests uses.
    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0x00, 0x00, 0xFF, 0xD9];

    public string? BackendName => "fake";

    public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => Task.FromResult(FakeJpeg);

    public Task<byte[]?> CaptureRawScreenAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);

    public (int Width, int Height, int Left, int Top) GetScreenSize() => (1920, 1080, 0, 0);
}
