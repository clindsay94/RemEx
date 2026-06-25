using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Security;

namespace Remex.Agent.Services.Security;

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
    private readonly string? _certPathOverride;

    public CertificateService(ILogger<CertificateService> logger)
    {
        _logger = logger;
    }

    internal CertificateService(ILogger<CertificateService> logger, string certPath)
    {
        _logger = logger;
        _certPathOverride = certPath;
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
        WriteProtectedFile(path, pfxBytes);

        // Return a new certificate loaded from the PFX (ensures private key is properly associated)
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    /// <summary>
    /// Atomically writes <paramref name="data"/> (the host's PFX private key) to
    /// <paramref name="path"/> with owner-only permissions established <b>before</b> any
    /// bytes are written. The data is first written to a sibling temporary file whose
    /// restrictive permissions are applied at/just after creation while the file is still
    /// empty, then atomically moved over the target. This eliminates the time-of-check /
    /// time-of-use window where the unprotected private key would otherwise be briefly
    /// readable by other accounts.
    /// </summary>
    private static void WriteProtectedFile(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path)!;
        // Unique temp file in the same directory so File.Move is an atomic, same-volume rename.
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Create the file with a restrictive ACL (LocalSystem + Administrators full
                // control, no inherited access) applied atomically AT creation, then write the
                // key material into that already-protected handle. The private key is never on
                // disk with default/inherited permissions.
                WriteWindowsProtectedFile(tempPath, data);
            }
            else
            {
                // Create the empty file 0600 (owner read/write only) BEFORE writing the key
                // material, so the private key is never momentarily world-readable.
                using (var fs = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    fs.Write(data);
                    fs.Flush(flushToDisk: true);
                }
                // Re-assert mode in case the umask/create raced; defensive and cheap.
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // Atomically replace the target. The temp file already carries the restricted
            // permissions/ACL, so the target is never exposed with default permissions.
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindowsProtectedFile(string tempPath, byte[] data)
    {
        var security = new FileSecurity();
        // Disable inheritance and drop any inherited rules so only the rules we add apply.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new FileSystemAccessRule(
            systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            adminsSid, FileSystemRights.FullControl, AccessControlType.Allow));

        // Create the file with the restricted ACL applied atomically at creation, then write
        // the key material into the already-protected handle.
        var fileInfo = new FileInfo(tempPath);
        using var fs = fileInfo.Create(
            FileMode.CreateNew,
            FileSystemRights.WriteData | FileSystemRights.WriteAttributes,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            security);
        fs.Write(data);
        fs.Flush();
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup; nothing actionable if the temp file lingers.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private static string ComputeSpkiHash(X509Certificate2 cert)
    {
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToBase64String(hash);
    }

    private string GetCertificatePath()
    {
        if (_certPathOverride is not null)
            return _certPathOverride;

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
