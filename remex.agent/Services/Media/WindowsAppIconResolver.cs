using System.Diagnostics;
using System.Runtime.Versioning;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Turns a Windows AUMID into the app's own icon, for when a session has no album art
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// THIS IS WHERE <c>SourceApp</c> FINALLY EARNS ITS PLACE ON THE WIRE. It has been carried since
/// RemEx-xx6xf as a label nobody drew; spec 2.1 makes it the second rung of the artwork ladder, so a
/// podcast app or a game with no cover still shows something the user recognises rather than a
/// generic glyph.
/// </para>
/// <para>
/// TWO KINDS OF AUMID, AND THEY ARE NOT ALIKE. A packaged app's is
/// <c>PackageFamilyName!AppId</c> — the exclamation mark is the tell — and it resolves through
/// <see cref="PackageManager"/> to the manifest logo, which is a designed asset at the size we ask
/// for and strictly better than anything icon extraction produces. A desktop app's AUMID is whatever
/// the app registered, commonly its executable name, and there is no registry that maps it to a
/// file; the only reliable bridge is the set of RUNNING processes, because the app whose media
/// session we are reading is by definition running.
/// </para>
/// <para>
/// EVERY FAILURE IS NULL, INCLUDING THE INTERESTING ONES. Enumerating packages can be refused,
/// <c>MainModule</c> throws for anything at a higher integrity level than the agent, and a manifest
/// can point at a logo that is not there. None of that is worth a log line once per track change,
/// and none of it may escape: <see cref="IMediaArtworkSource"/>'s contract is that artwork never
/// takes the sampler down.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class WindowsAppIconResolver
{
    /// <summary>
    /// The size asked of a packaged app's manifest logo.
    /// </summary>
    /// <remarks>
    /// 256 TO MATCH THE EXISTING DESKTOP ICON PATH (<c>DesktopIconExtractionService</c>), so the two
    /// rungs of the ladder produce comparably sized images and the phone's mini-player does not
    /// change apparent sharpness depending on which one hit.
    /// </remarks>
    private const int LogoEdge = 256;

    /// <summary>
    /// The icon bytes for <paramref name="aumid"/>, or null when there are none to be had.
    /// </summary>
    public static async Task<byte[]?> ResolveAsync(string? aumid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return null;
        }

        var separator = aumid.IndexOf('!', StringComparison.Ordinal);
        return separator > 0
            ? await ResolvePackagedLogoAsync(aumid, aumid[..separator], ct)
            : ResolveDesktopExecutableIcon(aumid);
    }

    /// <summary>Reads a WinRT image stream into bytes, subject to the store's size cap.</summary>
    /// <remarks>
    /// <para>
    /// SHARED WITH THE SESSION-THUMBNAIL RUNG in <c>WindowsMediaSessionReader</c>, because both rungs
    /// come back as an <see cref="IRandomAccessStreamReference"/> and the awkward part — sizing the
    /// read, capping it, and not leaking the stream — is identical.
    /// </para>
    /// <para>
    /// <c>DataReader</c> RATHER THAN A <c>Stream</c> ADAPTER, so nothing here depends on the
    /// WinRT-to-<c>System.IO</c> interop shims: the projection can do this on its own.
    /// </para>
    /// </remarks>
    internal static async Task<byte[]?> ReadImageStreamAsync(IRandomAccessStreamReference? reference, CancellationToken ct)
    {
        if (reference is null)
        {
            return null;
        }

        using var stream = await reference.OpenReadAsync().AsTask(ct);
        if (stream.Size == 0 || stream.Size > (ulong)MediaArtworkStore.MaxArtworkBytes)
        {
            return null;
        }

        var length = (uint)stream.Size;
        using var reader = new DataReader(stream);
        var loaded = await reader.LoadAsync(length).AsTask(ct);
        if (loaded != length)
        {
            return null;
        }

        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]?> ResolvePackagedLogoAsync(string aumid, string familyName, CancellationToken ct)
    {
        try
        {
            // The empty user SID means "the user this process is running as", which is the only user
            // whose packages the agent has any business enumerating.
            var packages = new PackageManager().FindPackagesForUser(string.Empty, familyName);

            foreach (var package in packages)
            {
                ct.ThrowIfCancellationRequested();

                var entries = await package.GetAppListEntriesAsync().AsTask(ct);

                // A package can export several entries; prefer the one whose AUMID we were given, and
                // fall back to the first, because a family name with one entry is the common case and
                // an exact-match-only rule would give up on it whenever the AppId spelling differs.
                var entry = entries.FirstOrDefault(
                        e => string.Equals(e.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault();

                if (entry is null)
                {
                    continue;
                }

                var logo = entry.DisplayInfo.GetLogo(new global::Windows.Foundation.Size(LogoEdge, LogoEdge));
                var bytes = await ReadImageStreamAsync(logo, ct);
                if (bytes is { Length: > 0 })
                {
                    return bytes;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A desktop app's icon, found by matching the AUMID against running processes.
    /// </summary>
    /// <remarks>
    /// THE MATCH IS ON THE PROCESS NAME, DELIBERATELY LOOSE. Apps register AUMIDs as
    /// <c>Spotify.exe</c>, <c>Spotify</c>, and occasionally a full path, and all three name the same
    /// executable. Comparing case-insensitively after stripping any directory and the <c>.exe</c>
    /// suffix covers every spelling that has actually shown up, and a miss costs a glyph rather than
    /// a wrong icon — there is no plausible way for this to match a DIFFERENT app.
    /// </remarks>
    private static byte[]? ResolveDesktopExecutableIcon(string aumid)
    {
        var name = aumid.Trim();

        var separator = name.LastIndexOfAny(['\\', '/']);
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (name.Length == 0)
        {
            return null;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                if (!string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? path;
                try
                {
                    // Throws for anything the agent cannot open — a higher-integrity process, or one
                    // that exited between the enumeration and here. Skip it and keep looking; there
                    // is often more than one process with the same name.
                    path = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var bytes = MediaArtworkFallback.ExtractedIconBytes(path);
                if (bytes is { Length: > 0 })
                {
                    return bytes;
                }
            }

            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
