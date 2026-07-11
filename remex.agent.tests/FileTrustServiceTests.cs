using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// WP2 coverage for <see cref="FileTrustService"/>: trust persistence, revoke, auto-revoke on unpair,
/// and the consent request / timeout flow (plan §2).
/// </summary>
public sealed class FileTrustServiceTests
{
    private static PairedClientRegistry PairedWith(string dir, params string[] clientIds)
    {
        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance,
            Path.Combine(dir, "paired_clients.json"));
        foreach (var id in clientIds)
            registry.RegisterClient(id);
        return registry;
    }

    private static FileTrustService NewService(
        PairedClientRegistry registry, string storePath, TimeSpan? consentTimeout = null)
        => new(NullLogger<FileTrustService>.Instance, registry, storePath,
            consentTimeout ?? TimeSpan.FromSeconds(5));

    private static FileConsentRequest FullBrowseRequest()
        => new() { ConsentId = Guid.NewGuid().ToString("N"), Kind = FileConsentKinds.FullBrowse };

    [Fact]
    public async Task FullBrowseGrant_PersistsAcrossInstances()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "file_transfer_trust.json");
            var registry = PairedWith(dir.FullName, "client-a");

            var svc = NewService(registry, storePath);
            await svc.SetFullBrowseGrantedAsync("client-a", true, default);

            // A brand-new instance against the same store + registry must see the grant.
            var reloaded = NewService(registry, storePath);
            Assert.True(await reloaded.IsFullBrowseGrantedAsync("client-a", default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Revoke_ClearsAllGrants()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "file_transfer_trust.json");
            var registry = PairedWith(dir.FullName, "client-a");
            var svc = NewService(registry, storePath);

            await svc.SetFullBrowseGrantedAsync("client-a", true, default);
            await svc.SetAutoAcceptIncomingAsync("client-a", true, default);
            Assert.True(await svc.IsFullBrowseGrantedAsync("client-a", default));

            await svc.RevokeAsync("client-a", default);

            Assert.False(await svc.IsFullBrowseGrantedAsync("client-a", default));
            Assert.False(await svc.IsAutoAcceptIncomingAsync("client-a", default));
            Assert.Null(await svc.GetTrustAsync("client-a", default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Grant_IsNotHonored_AndIsPruned_WhenClientNoLongerPaired()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "file_transfer_trust.json");

            var pairedRegistry = PairedWith(dir.FullName, "client-a");
            var svc = NewService(pairedRegistry, storePath);
            await svc.SetFullBrowseGrantedAsync("client-a", true, default);
            Assert.True(await svc.IsFullBrowseGrantedAsync("client-a", default));

            // Reload the persisted grant, but with a registry where the client is no longer paired.
            var emptyRegistry = new PairedClientRegistry(
                NullLogger<PairedClientRegistry>.Instance,
                Path.Combine(dir.FullName, "empty_paired_clients.json"));
            var reloaded = NewService(emptyRegistry, storePath);

            Assert.False(await reloaded.IsFullBrowseGrantedAsync("client-a", default));
            // Auto-revoke: the stale record must be pruned from enumeration too.
            Assert.Empty(await reloaded.GetAllAsync(default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RequestConsent_TimesOut_ToCleanDeny()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "file_transfer_trust.json");
            var registry = PairedWith(dir.FullName, "client-a");
            var svc = NewService(registry, storePath, TimeSpan.FromMilliseconds(50));

            // No ConsentRequested subscriber resolves it → the 50ms timeout must deny cleanly.
            var decision = await svc.RequestConsentAsync("client-a", FullBrowseRequest(), default);

            Assert.False(decision.Granted);
            Assert.False(await svc.IsFullBrowseGrantedAsync("client-a", default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RequestConsent_RaisesPrompt_AndGrantWithRememberPersists()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "file_transfer_trust.json");
            var registry = PairedWith(dir.FullName, "client-a");
            var svc = NewService(registry, storePath);

            string? promptedConsentId = null;
            string? promptedClientId = null;
            svc.ConsentRequested += prompt =>
            {
                promptedConsentId = prompt.Request.ConsentId;
                promptedClientId = prompt.ClientId;
            };

            var request = FullBrowseRequest();
            var pending = svc.RequestConsentAsync("client-a", request, default);

            // The prompt fires synchronously before the awaiting task suspends.
            Assert.Equal(request.ConsentId, promptedConsentId);
            Assert.Equal("client-a", promptedClientId);

            svc.ResolveConsent(request.ConsentId, granted: true, remember: true);
            var decision = await pending;

            Assert.True(decision.Granted);
            Assert.True(await svc.IsFullBrowseGrantedAsync("client-a", default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RequestConsent_WhenAlreadyGranted_ReturnsImmediateAcceptWithoutPrompt()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "file_transfer_trust.json");
            var registry = PairedWith(dir.FullName, "client-a");
            var svc = NewService(registry, storePath);
            await svc.SetFullBrowseGrantedAsync("client-a", true, default);

            var promptRaised = false;
            svc.ConsentRequested += _ => promptRaised = true;

            var decision = await svc.RequestConsentAsync("client-a", FullBrowseRequest(), default);

            Assert.True(decision.Granted);
            Assert.False(promptRaised);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RequestConsent_ResolvedAsDenied_DoesNotPersistGrant()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "file_transfer_trust.json");
            var registry = PairedWith(dir.FullName, "client-a");
            var svc = NewService(registry, storePath);

            var request = FullBrowseRequest();
            var pending = svc.RequestConsentAsync("client-a", request, default);
            svc.ResolveConsent(request.ConsentId, granted: false, remember: true);
            var decision = await pending;

            Assert.False(decision.Granted);
            Assert.False(await svc.IsFullBrowseGrantedAsync("client-a", default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
