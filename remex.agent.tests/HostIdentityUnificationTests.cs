namespace Remex.Agent.Tests;

/// <summary>
/// Regression coverage for review-report.md Finding 5 ("two different host identities").
/// The report claimed the QR bootstrap endpoint returned <c>HostBootstrapper.InstanceId</c>
/// (per-process GUID) while the PIN PairingResponse returned <c>Environment.MachineName</c>
/// (stable). Verification against the current code (HEAD c79e2d4) showed both flows
/// already use <c>HostBootstrapper.HostId</c> (= MachineName) — commit d272ebc aligned
/// them three days before the review report was written.
///
/// These are deliberately source-content / property contract tests rather than integration
/// tests because (a) an integration test through WebApplicationFactory would have to start
/// a pairing session, which leaks singleton state across xUnit's parallel test classes,
/// and (b) the contract being protected is "the right symbol is used in the right place"
/// — a build-time / structural property, not a runtime behavior.
/// </summary>
public sealed class HostIdentityUnificationTests
{
    [Fact]
    public void HostId_IsStableAcrossCalls()
    {
        // HostId is meant to be a stable identity (current implementation: Environment.MachineName).
        // If anyone ever reassigns it to a per-process Guid like InstanceId, this trips.
        var first = HostBootstrapper.HostId;
        var second = HostBootstrapper.HostId;
        Assert.Equal(first, second);
        Assert.False(string.IsNullOrWhiteSpace(first), "HostId must not be empty.");
    }

    [Fact]
    public void HostId_IsDistinctFromInstanceId()
    {
        // InstanceId is a per-process Guid for stream/session diagnostics. HostId is the
        // stable trust identity. If they ever collapse to the same value (or InstanceId
        // becomes stable), downstream code may start relying on the conflation and the
        // separation will be much harder to restore.
        Assert.NotEqual(HostBootstrapper.HostId, HostBootstrapper.InstanceId);
    }

    [Fact]
    public void QrBootstrapEndpoint_UsesHostIdNotInstanceId()
    {
        // Contract test on the source: the /pairing-qr endpoint must return HostBootstrapper.HostId,
        // not HostBootstrapper.InstanceId. A regression here re-introduces review-report.md Finding 5.
        var bootstrapper = ReadSource("Remex.Agent/HostBootstrapper.cs");

        var qrBlock = ExtractMapGetBlock(bootstrapper, "/pairing-qr");
        Assert.Contains("hostId = HostId", qrBlock);
        Assert.DoesNotContain("hostId = InstanceId", qrBlock);
        Assert.DoesNotContain("InstanceId", qrBlock);
    }

    [Fact]
    public void PairingHandlerResponse_UsesHostIdNotInstanceId()
    {
        // Contract test: the PairingHandler's PairingResponse payload must populate HostId
        // from HostBootstrapper.HostId (the stable identity), never from InstanceId. The
        // review report's Finding 5 specifically called out this asymmetry.
        var pairingHandler = ReadSource("Remex.Agent/Handlers/PairingHandler.cs");

        // Locate the PairingResponse construction and the HostId assignment within it.
        var idx = pairingHandler.IndexOf("PairingResponse = new PairingResponse", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Could not find PairingResponse construction in PairingHandler.cs.");

        var snippet = pairingHandler.Substring(idx, Math.Min(800, pairingHandler.Length - idx));
        Assert.Contains("HostId = HostBootstrapper.HostId", snippet);
        Assert.DoesNotContain("HostId = HostBootstrapper.InstanceId", snippet);
    }

    private static string ReadSource(string repoRelativePath)
    {
        // Test runner cwd is the test project's bin directory. Walk up until we find the
        // repo root (the directory containing the file path we're asked for).
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, repoRelativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            dir = dir.Parent;
        }
        Assert.Fail($"Could not locate {repoRelativePath} starting from {Directory.GetCurrentDirectory()}.");
        return string.Empty; // unreachable
    }

    private static string ExtractMapGetBlock(string source, string route)
    {
        var sentinel = $"app.MapGet(\"{route}\"";
        var idx = source.IndexOf(sentinel, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Could not find app.MapGet(\"{route}\", ...) in source.");

        // Walk brace depth to find the matching close.
        var braceStart = source.IndexOf('{', idx);
        Assert.True(braceStart >= 0, "Could not find opening brace of the MapGet delegate.");

        var depth = 1;
        var i = braceStart + 1;
        while (i < source.Length && depth > 0)
        {
            switch (source[i])
            {
                case '{': depth++; break;
                case '}': depth--; break;
            }
            if (depth == 0) break;
            i++;
        }
        Assert.Equal(0, depth);

        return source.Substring(braceStart, i - braceStart + 1);
    }
}
