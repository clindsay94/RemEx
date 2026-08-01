using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that a host which cannot enumerate its displays reports NO persistent key rather than
/// inventing one (RemEx-kiy1).
/// </summary>
/// <remarks>
/// <para>
/// The contract on <see cref="DesktopDisplayInfo.PersistentDisplayKey"/> is that an empty value means
/// "the host could not establish a stable identity for this display", and that a client must then
/// remember nothing rather than falling back to the session-scoped <c>DisplayId</c>. The fallback
/// catalog contradicted that by sending the literal <c>"default"</c> — a value that looks like an
/// identity and is not one.
/// </para>
/// <para>
/// THE COST OF GETTING THIS WRONG IS SMALL BUT THE DIRECTION MATTERS. Because the stored preference
/// is global rather than per-host (RemEx-ynur), a literal shared by every host that cannot enumerate
/// would match across machines. Here it resolves to the only display there is, so nothing visible
/// goes wrong — but it teaches the client that a key it cannot trust is a key, which is exactly the
/// habit RemEx-i50k removed on Windows.
/// </para>
/// <para>
/// Deliberately NOT asserted here: that the xrandr path stops sending the connector name. It keeps
/// sending it, because a DRM connector name is port-scoped rather than enumeration-scoped and is in
/// the same stability class as the Windows interface path. Mirroring the Windows change there would
/// have cost Linux users the remembered-monitor feature for no safety gain — see the remarks on
/// <see cref="DesktopDisplayInfo.PersistentDisplayKey"/>.
/// </para>
/// </remarks>
public class FallbackDisplayKeyTests
{
    /// <summary>
    /// The most degenerate host there is: it reports a screen size and implements nothing else, so it
    /// falls through to the interface's default catalog.
    /// </summary>
    private sealed class SizeOnlyCaptureService : IScreenCaptureService
    {
        public (int Width, int Height, int Left, int Top) GetScreenSize() => (1920, 1080, 0, 0);

        public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(System.Array.Empty<byte>());
    }

    private static DesktopDisplayInfo TheOnlyDisplay()
    {
        // Through the interface on purpose: GetDisplayCatalog is a DEFAULT INTERFACE METHOD, and the
        // default is exactly the implementation under test — the one a host reaches when it has
        // nothing better to offer.
        IScreenCaptureService service = new SizeOnlyCaptureService();
        var catalog = service.GetDisplayCatalog();
        return Assert.Single(catalog.Displays);
    }

    [Fact]
    public void AHostThatCannotEnumerateReportsNoPersistentKey()
    {
        // THE BEAD. Empty is the documented way to say "no stable identity"; "default" claimed one.
        Assert.Equal(string.Empty, TheOnlyDisplay().PersistentDisplayKey);
    }

    [Fact]
    public void TheDisplayIsStillSelectableBySessionId()
    {
        // Dropping the key must not drop the ability to pick the display. DisplayId is session-scoped
        // and stays populated — it is what selection uses; only PERSISTENCE is withheld.
        var display = TheOnlyDisplay();

        Assert.False(string.IsNullOrWhiteSpace(display.DisplayId));
        Assert.True(display.IsPrimary);
    }

    [Fact]
    public void AnEmptyKeySurvivesTheWireUnchanged()
    {
        // The envelope omits nulls but not empty strings, so an empty key must arrive as an empty
        // key rather than vanishing into a missing field the client could misread as absent.
        var original = new DesktopDisplayInfo
        {
            DisplayId = "default",
            PersistentDisplayKey = string.Empty,
            Name = "Display",
            IsPrimary = true,
            Width = 1920,
            Height = 1080,
        };

        var json = RemexJson.SerializeToUtf8Bytes(original, RemexJsonSerializerContext.Default.DesktopDisplayInfo);
        var round = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.DesktopDisplayInfo);

        Assert.NotNull(round);
        Assert.Equal(string.Empty, round!.PersistentDisplayKey);
        Assert.Equal("default", round.DisplayId);
    }
}
