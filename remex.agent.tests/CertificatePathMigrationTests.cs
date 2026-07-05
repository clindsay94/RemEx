using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

/// <summary>
/// RemEx-u0oc: on Linux the cert lives per-user (~/.local/share/Remex), and a cert left in the
/// legacy system path (/var/lib/remex, created only by the retired remex-host root service) is
/// migrated once — byte-identical, so the SPKI that phones pinned at pairing time is preserved.
/// An unreadable legacy cert must surface (brick canary), never be silently replaced.
/// </summary>
public sealed class CertificatePathMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _userPath;
    private readonly string _legacyDir;
    private readonly string _legacyPath;

    public CertificatePathMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remex-cert-migration-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _userPath = Path.Combine(_dir, "user", "cert.pfx");
        _legacyDir = Path.Combine(_dir, "legacy");
        _legacyPath = Path.Combine(_legacyDir, "cert.pfx");
    }

    public void Dispose()
    {
        // A test may have made the legacy cert unreadable; restore modes so cleanup succeeds.
        if (!OperatingSystem.IsWindows() && File.Exists(_legacyPath))
        {
            File.SetUnixFileMode(_legacyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Directory.Delete(_dir, recursive: true);
    }

    private static CertificateService CreateService(string certPath) =>
        new(NullLogger<CertificateService>.Instance, certPath);

    [Fact]
    public void Resolve_UserCertExists_ReturnsUserPathAndIgnoresLegacy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_userPath)!);
        File.WriteAllBytes(_userPath, [1, 2, 3]);
        Directory.CreateDirectory(_legacyDir);
        File.WriteAllBytes(_legacyPath, [4, 5, 6]);

        var resolved = CreateService(_userPath).ResolveNonWindowsCertificatePath(_userPath, _legacyPath);

        Assert.Equal(_userPath, resolved);
        Assert.True(File.Exists(_legacyPath)); // untouched — user cert is the active identity
    }

    [Fact]
    public void Resolve_NoCertsAnywhere_ReturnsUserPathWithoutCreatingFile()
    {
        var resolved = CreateService(_userPath).ResolveNonWindowsCertificatePath(_userPath, _legacyPath);

        Assert.Equal(_userPath, resolved);
        Assert.False(File.Exists(_userPath));
    }

    [Fact]
    public async Task Resolve_ReadableLegacyCert_MigratesPreservingSpki()
    {
        // Generate a REAL certificate at the legacy path, exactly like the old root service did.
        Directory.CreateDirectory(_legacyDir);
        var legacyService = CreateService(_legacyPath);
        await legacyService.GetOrCreateCertificateAsync(CancellationToken.None);
        var originalSpki = legacyService.GetSpkiSha256Base64();

        var resolved = CreateService(_userPath).ResolveNonWindowsCertificatePath(_userPath, _legacyPath);

        Assert.Equal(_userPath, resolved);
        Assert.True(File.Exists(_userPath));
        Assert.False(File.Exists(_legacyPath)); // temp legacy dir is writable → cleanup succeeds

        // The migrated cert must be the SAME identity: phones pinned this SPKI at pairing time.
        var migratedService = CreateService(_userPath);
        await migratedService.GetOrCreateCertificateAsync(CancellationToken.None);
        Assert.Equal(originalSpki, migratedService.GetSpkiSha256Base64());
    }

    [Fact]
    public async Task Resolve_ReadableLegacyCert_MigratedFileIsOwnerOnly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_legacyDir);
        await CreateService(_legacyPath).GetOrCreateCertificateAsync(CancellationToken.None);

        CreateService(_userPath).ResolveNonWindowsCertificatePath(_userPath, _legacyPath);

        var mode = File.GetUnixFileMode(_userPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Resolve_UnreadableLegacyCert_ReturnsLegacyPathSoBrickCanaryFires()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            // Mode 000 does not block root; the scenario only exists for regular users.
            return;
        }

        Directory.CreateDirectory(_legacyDir);
        File.WriteAllBytes(_legacyPath, [4, 5, 6]);
        File.SetUnixFileMode(_legacyPath, UnixFileMode.None);

        var resolved = CreateService(_userPath).ResolveNonWindowsCertificatePath(_userPath, _legacyPath);

        // Pointing at the unreadable file makes GetOrCreateCertificateAsync fail loudly with
        // repair instructions instead of minting a new identity that orphans paired phones.
        Assert.Equal(_legacyPath, resolved);
        Assert.False(File.Exists(_userPath));
    }
}
