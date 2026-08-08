using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// PC-host implementation of <see cref="IFileTrustService"/> (plan §2, WP2). Persists per-paired-device
/// file-sharing consent to <c>file_transfer_trust.json</c> in the machine-wide RemEx data directory
/// (beside <c>paired_clients.json</c>), and drives the local consent-prompt flow via
/// <see cref="ConsentRequested"/> / <see cref="ResolveConsent"/> with a 60-second auto-deny timeout.
/// Since RemEx-220r the prompt is ROUTED rather than always local: ConsentRoutePolicy decides whether
/// to ask the phone that asked, ask on this PC, or refuse without asking anyone.
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

    /// <summary>Who is connected, and who can be asked on their own screen (RemEx-xuyu).</summary>
    private readonly Remex.Agent.Services.ClientSessionRegistry _sessions;
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

    public FileTrustService(
        ILogger<FileTrustService> logger,
        PairedClientRegistry pairedClients,
        Remex.Agent.Services.ClientSessionRegistry sessions)
        : this(logger, pairedClients, sessions, storePath: null, consentTimeout: null)
    {
    }

    /// <summary>
    /// Test seam: overrides the store path and consent timeout so persistence and timeout behaviour can be
    /// exercised hermetically without waiting the production 60 seconds or touching machine-wide state.
    /// </summary>
    internal FileTrustService(
        ILogger<FileTrustService> logger,
        PairedClientRegistry pairedClients,
        Remex.Agent.Services.ClientSessionRegistry sessions,
        string? storePath,
        TimeSpan? consentTimeout)
    {
        _logger = Guard.NotNull(logger);
        _pairedClients = Guard.NotNull(pairedClients);
        _sessions = Guard.NotNull(sessions);
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
            // ASK WHOEVER IS ACTUALLY IN A POSITION TO ANSWER (RemEx-220r). The decision belongs to the
            // person who made the request, and until now it was always put in front of the PC — which,
            // when the phone user is in another room, means a dialog waiting for nobody until it times
            // out. The rule itself lives in ConsentRoutePolicy with its own tests; this is the wiring.
            var route = ConsentRoutePolicy.Route(
                _sessions.IsConnected(clientId),
                _sessions.SupportsPhonePrompt(clientId));

            switch (route)
            {
                case ConsentRoute.Deny:
                    // The asker is gone. Not a prompt anywhere: a PC dialog would ask about a transfer
                    // that can no longer happen, and an "allow" would grant durable trust to a device
                    // that is not there. See ConsentRoutePolicy.Route.
                    //
                    // AND IT SAYS WHY (RemEx-l580). A bare deny here is byte-identical to the PC user
                    // tapping Deny, so the phone that asked seconds ago can only show a flat no for a
                    // state its user could have fixed.
                    _logger.LogInformation(
                        "Denying file consent {ConsentId}: the requesting client is no longer connected.",
                        request.ConsentId);
                    return new FileConsentDecision(
                        Granted: false, Remember: false, DenyReason: FileConsentDenyReasons.ClientUnreachable);

                case ConsentRoute.Phone:
                    // STAMP THE DEADLINE ON THE WIRE COPY (RemEx-6mxu). The PC branch below has always
                    // been handed expiresAtUnixMs; the phone branch sent a prompt with no expiry at all,
                    // so a phone could render it, wait, and tap Allow after this side had already timed
                    // out into a deny — TryResolveRemoteConsent then returns false into a log line and
                    // the two devices disagree with nothing on either screen saying so.
                    //
                    // Computed BEFORE the send while the timeout starts after it, so the advertised
                    // deadline is a hair EARLIER than the real one. That asymmetry is the safe
                    // direction: a prompt that gives up slightly early is a clean deny both sides
                    // agree on, whereas a late one is exactly the disagreement above.
                    //
                    // The pending entry keeps the caller's unstamped request: expiry is a property of
                    // this attempt to ask, not of what was asked, and only the remote renderer needs it.
                    var prompt = new RemexMessage
                    {
                        Type = MessageTypes.FileConsentRequest,
                        FileConsentRequest = request with { ExpiresAtUnixMs = expiresAtUnixMs },
                    };

                    if (!await _sessions.TrySendAsync(clientId, prompt, ct))
                    {
                        // DENY RATHER THAN FALL BACK TO THE PC. A failed send means the client went
                        // away between the routing decision and this line — which is the same state
                        // Route already calls a deny, for the same reason. Falling back would put a
                        // question on the PC about an asker who is not there.
                        //
                        // Same reason code as that branch, deliberately: the two are told apart in
                        // this log because the difference is diagnostic, and are one answer on the
                        // wire because the client's next step for both is to reconnect and ask again.
                        _logger.LogInformation(
                            "Denying file consent {ConsentId}: could not reach the requesting client.",
                            request.ConsentId);
                        return new FileConsentDecision(
                            Granted: false, Remember: false, DenyReason: FileConsentDenyReasons.ClientUnreachable);
                    }

                    break;

                case ConsentRoute.Desktop:
                default:
                    // The compatibility path, and NOT dead code: a phone built before the phone-side
                    // sheet cannot render the prompt, so it must still be asked somewhere it can be
                    // answered. Worse UX that is reachable beats better UX that nobody can answer.
                    //
                    // With no subscriber — a headless host, or before the dialog is wired — the prompt
                    // simply times out into a clean deny.
                    ConsentRequested?.Invoke(new FileConsentPrompt
                    {
                        ClientId = clientId,
                        Request = request,
                        ExpiresAtUnixMs = expiresAtUnixMs,
                    });
                    break;
            }

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
    public bool TryResolveRemoteConsent(string? clientId, string? consentId, bool granted, bool remember)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(consentId)) return false;

        // BOUND TO THE CLIENT THE QUESTION WAS ASKED OF, which the local overload deliberately does not
        // check. A decision typed on the PC is the PC user's to make about anything; a decision arriving
        // over the wire is only the answer to the prompt THIS client was sent. Without the check, any
        // paired device that learned a consentId could answer somebody else's prompt — and the answer
        // that matters is "allow", which would hand it the access the other user was being asked about.
        if (!_pendingConsents.TryGetValue(consentId, out var pending)) return false;

        // The id rule — ordinal, and a resolved prompt is gone from the dictionary so a late answer
        // cannot revive it — is stated once in ConsentRoutePolicy with its own tests. Restating it
        // here is how two copies of a consent rule drift apart.
        if (!ConsentRoutePolicy.ShouldApplyResponse(consentId, pending.Request.ConsentId)) return false;
        if (!string.Equals(pending.ClientId, clientId, StringComparison.Ordinal)) return false;

        // TrySetResult's own answer, not an assumption: another responder — the PC dialog, or the
        // timeout — can win between the lookup above and here, and reporting "applied" then would log
        // a resolution that did not happen.
        return pending.Completion.TrySetResult(new FileConsentDecision(granted, remember));
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

                // Per-write staging name rather than a fixed <store>.tmp: a concurrent writer of the
                // same store must not be able to truncate this one mid-write and publish a partial
                // grant record (RemEx-kow1). Throws on failure into the catch blocks below, exactly
                // as the inline write it replaced did.
                RemexDataPaths.WriteAllTextAtomic(_storePath, json);

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
