using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Security;

namespace Remex.Host.Services.Security;

/// <summary>
/// Manages a self-signed TLS certificate for the host.
/// Cert is generated on first start and persisted to disk.
/// </summary>
public sealed class CertificateService : ICertificateService
{
    private readonly ILogger<CertificateService> _logger;
    private X509Certificate2? _certificate;
    private string? _spkiHashBase64;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CertificateService(ILogger<CertificateService> logger)
    {
        _logger = logger;
    }

    public async Task<X509Certificate2> GetOrCreateCertificateAsync(CancellationToken ct)
    {
        if (_certificate is not null)
            return _certificate;

        await _lock.WaitAsync(ct);
        try
        {
            if (_certificate is not null)
                return _certificate;

            var certPath = GetCertificatePath();
            var certDir = Path.GetDirectoryName(certPath)!;

            if (!Directory.Exists(certDir))
                Directory.CreateDirectory(certDir);

            if (File.Exists(certPath))
            {
                _logger.LogInformation("Loading existing certificate from {Path}", certPath);
                _certificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, null);
                _logger.LogInformation("Certificate loaded. Subject={Subject}, Expires={Expiry}",
                    _certificate.Subject, _certificate.NotAfter);
            }
            else
            {
                _logger.LogInformation("Generating new self-signed certificate at {Path}", certPath);
                _certificate = GenerateAndSaveCertificate(certPath);
                _logger.LogInformation("Certificate generated. Subject={Subject}, Expires={Expiry}",
                    _certificate.Subject, _certificate.NotAfter);
            }

            // Pre-compute the SPKI hash
            _spkiHashBase64 = ComputeSpkiHash(_certificate);
            _logger.LogInformation("Certificate SPKI SHA-256: {Hash}", _spkiHashBase64);

            return _certificate;
        }
        finally
        {
            _lock.Release();
        }
    }

    public string GetSpkiSha256Base64()
    {
        if (_spkiHashBase64 is null)
            throw new InvalidOperationException("Certificate not yet loaded. Call GetOrCreateCertificateAsync first.");
        return _spkiHashBase64;
    }

    public async Task RegenerateAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var certPath = GetCertificatePath();
            if (File.Exists(certPath))
                File.Delete(certPath);

            _certificate?.Dispose();
            _certificate = null;
            _spkiHashBase64 = null;

            _logger.LogInformation("Regenerating certificate at {Path}", certPath);
            _certificate = GenerateAndSaveCertificate(certPath);
            _spkiHashBase64 = ComputeSpkiHash(_certificate);
            _logger.LogInformation("Certificate regenerated. SPKI SHA-256: {Hash}", _spkiHashBase64);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static X509Certificate2 GenerateAndSaveCertificate(string path)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=RemExHost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                critical: false));

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(5));

        var pfxBytes = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, pfxBytes);

        // Set restricted permissions on Linux
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Return a new certificate loaded from the PFX (ensures private key is properly associated)
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    private static string ComputeSpkiHash(X509Certificate2 cert)
    {
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToBase64String(hash);
    }

    private static string GetCertificatePath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemEx", "cert.pfx");
        }

        // Linux: prefer /var/lib/remex, fallback to local app data
        const string systemPath = "/var/lib/remex";
        if (Directory.Exists(systemPath) || TryCreateDirectory(systemPath))
        {
            return Path.Combine(systemPath, "cert.pfx");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remex", "cert.pfx");
    }

    private static bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
