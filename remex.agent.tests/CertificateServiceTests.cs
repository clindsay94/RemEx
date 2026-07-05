using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

public sealed class CertificateServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _certPath;

    public CertificateServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remex-cert-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _certPath = Path.Combine(_dir, "test-cert.pfx");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private CertificateService CreateService() =>
        new(NullLogger<CertificateService>.Instance, _certPath);

    [Fact]
    public async Task GetOrCreate_FirstCall_GeneratesCertOnDisk()
    {
        var svc = CreateService();

        await svc.GetOrCreateCertificateAsync(CancellationToken.None);

        Assert.True(File.Exists(_certPath));
    }

    // PAIR-3 (RemEx-lr9): on Linux/Unix the private-key PFX must be written owner-only (0600) so the
    // key is never group/world-readable. Verifies the CertificateService.WriteProtectedFile Unix
    // branch produces exactly UserRead|UserWrite on real hardware. No-op off Linux (Windows uses ACLs).
    [Fact]
    public async Task GetOrCreate_OnLinux_WritesCertWith0600Permissions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var svc = CreateService();

        await svc.GetOrCreateCertificateAsync(CancellationToken.None);

        var mode = File.GetUnixFileMode(_certPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task GetOrCreate_SecondCall_ReturnsSameCachedInstance()
    {
        var svc = CreateService();

        var first = await svc.GetOrCreateCertificateAsync(CancellationToken.None);
        var second = await svc.GetOrCreateCertificateAsync(CancellationToken.None);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetOrCreate_NewInstance_LoadsExistingCertFromDisk()
    {
        var svc1 = CreateService();
        await svc1.GetOrCreateCertificateAsync(CancellationToken.None);
        var originalSpki = svc1.GetSpkiSha256Base64();

        var svc2 = CreateService();
        await svc2.GetOrCreateCertificateAsync(CancellationToken.None);

        Assert.Equal(originalSpki, svc2.GetSpkiSha256Base64());
    }

    [Fact]
    public async Task GetSpkiSha256Base64_IsDeterministicForSameCert()
    {
        var svc = CreateService();
        await svc.GetOrCreateCertificateAsync(CancellationToken.None);

        var hash1 = svc.GetSpkiSha256Base64();
        var hash2 = svc.GetSpkiSha256Base64();

        Assert.Equal(hash1, hash2);
        Assert.False(string.IsNullOrEmpty(hash1));
    }

    [Fact]
    public void GetSpkiSha256Base64_BeforeCertLoad_ThrowsInvalidOperation()
    {
        var svc = CreateService();

        Assert.Throws<InvalidOperationException>(() => svc.GetSpkiSha256Base64());
    }

    [Fact]
    public async Task RegenerateAsync_ProducesNewCertWithDifferentSpki()
    {
        var svc = CreateService();
        await svc.GetOrCreateCertificateAsync(CancellationToken.None);
        var originalSpki = svc.GetSpkiSha256Base64();

        await svc.RegenerateAsync(CancellationToken.None);
        var newSpki = svc.GetSpkiSha256Base64();

        Assert.NotEqual(originalSpki, newSpki);
        Assert.True(File.Exists(_certPath));
    }

    [Fact]
    public async Task RegenerateAsync_NewCertPersistedToDisk()
    {
        var svc = CreateService();
        await svc.GetOrCreateCertificateAsync(CancellationToken.None);
        await svc.RegenerateAsync(CancellationToken.None);
        var spkiAfterRegen = svc.GetSpkiSha256Base64();

        var svc2 = CreateService();
        await svc2.GetOrCreateCertificateAsync(CancellationToken.None);

        Assert.Equal(spkiAfterRegen, svc2.GetSpkiSha256Base64());
    }
}
