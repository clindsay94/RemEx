using System.Security.Cryptography.X509Certificates;

namespace Remex.Core.Services.Security;

public interface ICertificateService
{
    Task<X509Certificate2> GetOrCreateCertificateAsync(CancellationToken ct);
    string GetSpkiSha256Base64();
    Task RegenerateAsync(CancellationToken ct);
}
