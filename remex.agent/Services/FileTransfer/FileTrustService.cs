using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// PC-host implementation of <see cref="IFileTrustService"/> (plan §2, WP2). Persists per-paired-device
/// file-sharing consent to <c>file_transfer_trust.json</c> in the machine-wide RemEx data directory
/// (beside <c>paired_clients.json</c>), and drives the local consent-prompt flow via
/// <see cref="ConsentRequested"/> / <see cref="ResolveConsent"/> with a 60-second auto-deny timeout.
///
/// <para>
/// Grants are auto-revoked when a device is unpaired: every trust lookup is filtered through the
/// <see cref="PairedClientRegistry"/>, and a record for a client that is no longer paired is pruned on
/// access. This keeps the behaviour correct without coupling to (or modifying) the pairing registry's
/// internals — there is no unpair event to subscribe to.
/// </para>
///
/// <para>This service lives in <c>remex.agent</c> and is NOT NativeAOT-constrained; it uses the same
/// reflection-based <see cref="JsonSerializer"/> configuration as the sibling
/// <see cref="FileTransferService"/> for its on-disk store (the store is host-local state, never a wire
/// message).</para>
/// </summary>
public sealed class FileTrustService : IFileTrustService
{
    /// <summary>Wall-clock time an unanswered consent prompt waits before auto-denying (plan §2).</summary>
    private static readonly TimeSpan DefaultConsentTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<FileTrustService> _logger;
    private readonly PairedClientRegistry _pairedClients;
    private readonly string _storePath;
    private readonly TimeSpan _consentTimeout;

    private readonly object _persistenceGate = new();
    private readonly ConcurrentDictionary<string, FileTrustRecord> _trust = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingConsent> _pendingConsents = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event Action<FileConsentPrompt>? ConsentRequested;

    private sealed record PendingConsent
    {
        public required string ClientId { get; init; }
        public required FileConsentRequest Request { get; init; }
        public required TaskCompletionSource<FileConsentDecision> Completion { get; init; }
    }

    /// <summary>On-disk shape of one trust entry; the store is a <c>{ clientId: entry }</c> map.</summary>
    private sealed record TrustEntryDto
    {
        public bool FullBrowseGranted { get; init; }
        public bool AutoAcceptIncoming { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
    }

    public FileTrustService(ILogger<FileTrustService> logger, PairedClientRegistry pairedClients)
        : this(logger, pairedClients, storePath: null, consentTimeout: null)
    {
    }

    /// <summary>
    /// Test seam: overrides the store path and consent timeout so persistence and timeout behaviour can be
    /// exercised hermetically without waiting the production 60 seconds or touching machine-wide state.
    /// </summary>
    internal FileTrustService(
        ILogger<FileTrustService> logger,
        PairedClientRegistry pairedClients,
        string? storePath,
        TimeSpan? consentTimeout)
    {
        _logger = Guard.NotNull(logger);
        _pairedClients = Guard.NotNull(pairedClients);
        _consentTimeout = consentTimeout ?? DefaultConsentTimeout;

        if (storePath is null)
        {
            // Machine-wide store on Windows (C:\ProgramData\RemEx) beside paired_clients.json, so the
            // grants survive the host running under different accounts; per-user path elsewhere.
            var legacyFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
            var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
            RemexDataPaths.TryMigrateWindowsFile("file_transfer_trust.json");
            _storePath = Path.Combine(baseFolder, "file_transfer_trust.json");
        }
        else
        {
            _storePath = storePath;
        }

        LoadFromDisk();
    }

    // ── Queries ───────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<FileTrustRecord?> GetTrustAsync(string clientId, CancellationToken ct)
        => Task.FromResult(GetLiveRecord(clientId));

    /// <inheritdoc />
    public Task<IReadOnlyList<FileTrustRecord>> GetAllAsync(CancellationToken ct)
    {
        PruneUnpaired();
        IReadOnlyList<FileTrustRecord> all = _trust.Values
            .OrderBy(r => r.ClientId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(all);
    }

    /// <inheritdoc />
    public Task<bool> IsFullBrowseGrantedAsync(string clientId, CancellationToken ct)
        => Task.FromResult(GetLiveRecord(clientId)?.FullBrowseGranted == true);

    /// <inheritdoc />
    public Task<bool> IsAutoAcceptIncomingAsync(string clientId, CancellationToken ct)
        => Task.FromResult(GetLiveRecord(clientId)?.AutoAcceptIncoming == true);

    // ── Mutations ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task SetFullBrowseGrantedAsync(string clientId, bool granted, CancellationToken ct)
    {
        Upsert(clientId, rec => rec with { FullBrowseGranted = granted, UpdatedUtc = DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAutoAcceptIncomingAsync(string clientId, bool autoAccept, CancellationToken ct)
    {
        Upsert(clientId, rec => rec with { AutoAcceptIncoming = autoAccept, UpdatedUtc = DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RevokeAsync(string clientId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && _trust.TryRemove(clientId, out _))
        {
            PersistToDisk();
            _logger.LogInformation("Revoked all file-transfer trust for client {ClientId}.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
        }
        return Task.CompletedTask;
    }

    // ── Consent flow ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FileConsentDecision> RequestConsentAsync(
        string clientId, FileConsentRequest request, CancellationToken ct)
    {
        Guard.NotNull(request);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(request.ConsentId))
            return new FileConsentDecision(Granted: false, Remember: false);

        // A remembered grant already covers this kind of access → immediate accept, no prompt.
        if (IsAlreadyGranted(clientId, request.Kind))
            return new FileConsentDecision(Granted: true, Remember: true);

        var pending = new PendingConsent
        {
            ClientId = clientId,
            Request = request,
            Completion = new TaskCompletionSource<FileConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        _pendingConsents[request.ConsentId] = pending;

        var expiresAtUnixMs = DateTimeOffset.UtcNow.Add(_consentTimeout).ToUnixTimeMilliseconds();

        try
        {
            // Raise the local consent UI on this (serving) device. With no subscriber — e.g. a headless
            // host or Phase A before the dialog is wired — the prompt simply times out into a clean deny.
            ConsentRequested?.Invoke(new FileConsentPrompt
            {
                ClientId = clientId,
                Request = request,
                ExpiresAtUnixMs = expiresAtUnixMs,
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_consentTimeout);

            FileConsentDecision decision;
            try
            {
                decision = await pending.Completion.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // 60-second timeout (or caller cancellation) with no user response → clean deny.
                return new FileConsentDecision(Granted: false, Remember: false);
            }

            if (decision.Granted && decision.Remember)
                PersistGrant(clientId, request.Kind);

            return decision;
        }
        finally
        {
            _pendingConsents.TryRemove(request.ConsentId, out _);
        }
    }

    /// <inheritdoc />
    public void ResolveConsent(string consentId, bool granted, bool remember)
    {
        if (string.IsNullOrWhiteSpace(consentId))
            return;

        if (_pendingConsents.TryGetValue(consentId, out var pending))
            pending.Completion.TrySetResult(new FileConsentDecision(granted, remember));
    }

    // ── Internals ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the trust record for <paramref name="clientId"/> only while the client is still paired.
    /// A record for an unpaired (or blank) client is pruned and null is returned — this is the automatic
    /// revoke-on-unpair path.
    /// </summary>
    private FileTrustRecord? GetLiveRecord(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return null;

        if (!_pairedClients.IsClientPaired(clientId))
        {
            if (_trust.TryRemove(clientId, out _))
            {
                PersistToDisk();
                _logger.LogInformation(
                    "Pruned file-transfer trust for client {ClientId}: no longer paired.", clientId);
            }
            return null;
        }

        return _trust.TryGetValue(clientId, out var rec) ? rec : null;
    }

    private bool IsAlreadyGranted(string clientId, string kind)
    {
        var rec = GetLiveRecord(clientId);
        if (rec is null)
            return false;

        return kind switch
        {
            FileConsentKinds.FullBrowse => rec.FullBrowseGranted,
            FileConsentKinds.IncomingPush => rec.AutoAcceptIncoming,
            _ => false,
        };
    }

    private void PersistGrant(string clientId, string kind)
    {
        switch (kind)
        {
            case FileConsentKinds.FullBrowse:
                Upsert(clientId, rec => rec with { FullBrowseGranted = true, UpdatedUtc = DateTimeOffset.UtcNow });
                break;
            case FileConsentKinds.IncomingPush:
                Upsert(clientId, rec => rec with { AutoAcceptIncoming = true, UpdatedUtc = DateTimeOffset.UtcNow });
                break;
        }
    }

    private void Upsert(string clientId, Func<FileTrustRecord, FileTrustRecord> mutate)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return;

        var existing = _trust.TryGetValue(clientId, out var rec)
            ? rec
            : new FileTrustRecord { ClientId = clientId, UpdatedUtc = DateTimeOffset.UtcNow };

        _trust[clientId] = mutate(existing);
        PersistToDisk();
    }

    private void PruneUnpaired()
    {
        var removedAny = false;
        foreach (var clientId in _trust.Keys.ToList())
        {
            if (!_pairedClients.IsClientPaired(clientId) && _trust.TryRemove(clientId, out _))
                removedAny = true;
        }
        if (removedAny)
            PersistToDisk();
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storePath))
                return;

            var json = File.ReadAllText(_storePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, TrustEntryDto>>(json, JsonOptions);
            if (entries is null)
                return;

            foreach (var kvp in entries)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value is null)
                    continue;

                _trust[kvp.Key] = new FileTrustRecord
                {
                    ClientId = kvp.Key,
                    FullBrowseGranted = kvp.Value.FullBrowseGranted,
                    AutoAcceptIncoming = kvp.Value.AutoAcceptIncoming,
                    UpdatedUtc = kvp.Value.UpdatedUtc,
                };
            }

            _logger.LogInformation(
                "Loaded {Count} file-transfer trust record(s) from {Path}.", _trust.Count, _storePath);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse file-transfer trust store at {Path}; starting with an empty store.", _storePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "Failed to load file-transfer trust store at {Path}; starting with an empty store.", _storePath);
        }
    }

    private void PersistToDisk()
    {
        lock (_persistenceGate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var entries = _trust
                    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        kvp => kvp.Key,
                        kvp => new TrustEntryDto
                        {
                            FullBrowseGranted = kvp.Value.FullBrowseGranted,
                            AutoAcceptIncoming = kvp.Value.AutoAcceptIncoming,
                            UpdatedUtc = kvp.Value.UpdatedUtc,
                        },
                        StringComparer.Ordinal);

                var json = JsonSerializer.Serialize(entries, JsonOptions);
                var tempPath = _storePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _storePath, overwrite: true);

                // Live beside the sensitive paired-clients store in ProgramData; reuse its hardening so
                // other local users cannot flip a client's grants. Best-effort (logs, never throws).
                PairedClientRegistry.RestrictStorePermissions(_storePath, _logger);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to persist file-transfer trust store at {Path}.", _storePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "No access to persist file-transfer trust store at {Path}.", _storePath);
            }
        }
    }
}
