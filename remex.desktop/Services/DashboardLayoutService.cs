using System.Diagnostics;
using System.Text.Json;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

/// <summary>
/// JSON-file-based implementation of <see cref="IDashboardLayoutService"/>.
/// Writes are debounced so that rapid card movements don't cause excessive I/O.
/// </summary>
public sealed class DashboardLayoutService : IDashboardLayoutService, IDisposable
{
    /// <summary>
    /// The only options a profile is ever read or written with.
    /// </summary>
    /// <remarks>
    /// INTERNAL RATHER THAN PRIVATE SO THAT NOTHING HAS TO GUESS AT IT. Both the startup theme
    /// reader and the migration tests deserialise profiles, and both used to build their own
    /// <c>JsonSerializerOptions</c> — the tests with the defaults, which match property names only
    /// by coincidence. A camelCase policy that lives in one place is a contract; three copies of it
    /// is three chances for a real profile to read differently from a tested one.
    /// </remarks>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Brings a profile's customization to the current schema. A pure transform — it touches no disk.
    /// </summary>
    /// <returns>
    /// The same instance when nothing needed changing, a rewritten copy otherwise, and <c>null</c>
    /// for a <c>null</c> input. <paramref name="outcome"/> says which.
    /// </returns>
    internal static DashboardProfile? MigrateProfile(DashboardProfile? profile, out MigrationOutcome outcome)
    {
        outcome = default;
        if (profile is null) return null;

        var migrated = CustomizationMigration.Migrate(profile.Customization, out var warning);
        outcome = new MigrationOutcome(
            Changed: !ReferenceEquals(migrated, profile.Customization),
            Warning: warning);

        return outcome.Changed ? profile with { Customization = migrated } : profile;
    }

    /// <summary>What <see cref="MigrateProfile"/> had to do.</summary>
    /// <param name="Changed">
    /// Whether the record was rewritten. False means it was already current, and the caller has
    /// nothing to persist — which is the ordinary case on every launch after the first.
    /// </param>
    /// <param name="Warning">A value that had to be repaired, or <c>null</c>.</param>
    internal readonly record struct MigrationOutcome(bool Changed, string? Warning);

    /// <summary>
    /// Reads a profile from disk and brings its customization to the current schema. The one path
    /// that turns bytes on disk into a profile this app will paint from.
    /// </summary>
    /// <remarks>
    /// EVERY DESERIALIZE MUST COME THROUGH HERE, and it is a review finding that they did not
    /// (RemEx-dbkzy). <c>App.ApplyThemeBeforeWindowShown</c> had its own read-and-apply so the
    /// window could open on the saved theme rather than the default one — and it applied the RAW
    /// record. For a 2.4 Cyber-NOC profile that means the window opens violet and turns cyan a few
    /// milliseconds later when <see cref="LoadAsync"/> lands, or opens cyan, depending on how the
    /// file I/O races the window: the acceptance criterion for this bead is the phrase "opens on",
    /// and a second unmigrated reader is exactly how that fails while every test passes.
    /// </remarks>
    /// <returns><c>null</c> when the file does not exist. Throws on unreadable or malformed JSON.</returns>
    internal static DashboardProfile? ReadAndMigrate(string filePath, out MigrationOutcome outcome)
    {
        outcome = default;
        if (!File.Exists(filePath)) return null;

        return MigrateProfile(
            JsonSerializer.Deserialize<DashboardProfile>(File.ReadAllText(filePath), JsonOptions), out outcome);
    }

    private readonly string _filePath;
    private readonly ThemeService _themeService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _debounceTimer;
    private DashboardProfile? _pendingProfile;

    /// <summary>
    /// Guards <see cref="_debounceTimer"/>, <see cref="_pendingProfile"/> and
    /// <see cref="_saveGeneration"/>, which are written from the caller's thread and from the timer
    /// callback's.
    /// </summary>
    private readonly object _saveQueueLock = new();

    /// <summary>
    /// Bumped whenever the save queue changes. A debounced write captures it and re-checks it after
    /// acquiring <see cref="_gate"/>; a mismatch means something superseded it while it waited.
    /// </summary>
    /// <remarks>
    /// CANCELLING THE QUEUE IS NOT ENOUGH ON ITS OWN, and the reason is a review finding.
    /// <see cref="FlushAsync"/> takes the pending profile and clears the fields under the lock, then
    /// awaits the write OUTSIDE it - so between those two points the profile exists only as a local
    /// and <see cref="CancelPendingSave"/> has nothing left to null out. A savefile import landing
    /// in that window finds an empty queue, cancels nothing, and both writes race for the gate;
    /// SemaphoreSlim is not FIFO, so the older profile can land on top of the import. That is the
    /// same silent revert the cancel was added to stop, with the window cut from two seconds to the
    /// length of a preemption rather than closed.
    /// <para>
    /// A generation captured before the wait and re-checked after it decides the order under the
    /// gate, which is the only place that decision is actually safe to make.
    /// </para>
    /// </remarks>
    private long _saveGeneration;
    private const int DebounceMs = 2000;

    /// <summary>
    /// The currently loaded dashboard profile.
    /// Updated after calling <see cref="LoadAsync"/>.
    /// </summary>
    public DashboardProfile CurrentProfile { get; private set; } = new();

    /// <summary>
    /// Set when <see cref="LoadAsync"/> falls back to defaults due to a corrupt or unreadable file.
    /// <c>null</c> when the last load succeeded.
    /// </summary>
    public string? LoadFailureWarning { get; private set; }

    /// <summary>
    /// True when the most recent <see cref="LoadAsync"/> found no <c>dashboard_layout.json</c> on
    /// disk at all (as opposed to a corrupt file). Used by the first-run restore prompt to decide
    /// whether to offer restoring from the latest rolling auto-snapshot.
    /// </summary>
    public bool ProfileFileMissingOnLoad { get; private set; }

    /// <summary>
    /// Set when the most recent load could not read an EXISTING profile file, so
    /// <see cref="CurrentProfile"/> is a fabricated default rather than the user's real one
    /// (RemEx-8y3qy round 2). Left false on a genuinely fresh install
    /// (<see cref="ProfileFileMissingOnLoad"/>) — a brand-new user has no real profile to protect, and
    /// refusing their first saves would just be a second bug.
    /// </summary>
    /// <remarks>
    /// GUARDS <see cref="RequestSave"/> AND <see cref="SaveInternalAsync"/>, both. Every
    /// read-modify-write save in the app (<c>ShellViewModel.CompleteTutorial</c>/
    /// <c>OnIsReducedMotionChanged</c>, <c>CanvasDashboardViewModel.TriggerSave</c>/
    /// <c>DismissCoachMark</c>) builds its new profile from <see cref="CurrentProfile"/> with no way
    /// to tell "this is the real, freshly-loaded profile" from "this is the fallback default" — so
    /// while this flag is set, any of them would persist the fabricated default over whatever the
    /// user actually had saved. Refusing the write here costs the user only the single change they
    /// just made in THIS session; letting it through would have cost them the entire saved profile.
    /// The flag clears the moment a load actually succeeds, so ordinary saving resumes as soon as the
    /// file is readable again.
    /// </remarks>
    private volatile bool _profileIsFallback;

    /// <summary>Raised after a profile is successfully written to disk by <see cref="SaveInternalAsync"/> (i.e. after <see cref="SaveAsync"/> or a flushed <see cref="RequestSave"/>).</summary>
    public event Action? ProfileSaved;

    /// <summary>
    /// The layout file: the per-user RemEx directory, or the test redirect when it is set. A test
    /// that constructs this service used to create and overwrite the developer's own saved dashboard
    /// (RemEx-ln0k) — this is that user's arrangement of their cards, not scratch state.
    /// </summary>
    /// <remarks>
    /// INTERNAL RATHER THAN PRIVATE, AND WITHOUT A ForTests ALIAS (RemEx-mzbn). App reads this file
    /// before the window is shown, to apply the saved theme, and used to hand-build the path from
    /// SpecialFolder.LocalApplicationData — bypassing the redirect entirely. Two resolvers for one
    /// file, only one honouring the redirect, is the exact shape that made RemEx-ln0k necessary. The
    /// alias is gone with it: an alias can be asserted while production reads something else, so the
    /// redirection test now pins the member the app actually uses.
    /// </remarks>
    internal static string DefaultFilePath =>
        Path.Combine(RemexDataPaths.PerUserDirectory, "dashboard_layout.json");

    /// <summary>The file this instance actually resolved, so a test can pin the constructor.</summary>
    internal string FilePathForTests => _filePath;

    public DashboardLayoutService(ThemeService themeService)
    {
        _themeService = themeService;

        _filePath = DefaultFilePath;

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    /// <summary>How many times a read of an existing profile retries after a transient I/O failure.</summary>
    private const int TransientReadAttempts = 3;

    /// <summary>Delay between transient-read retries.</summary>
    private const int TransientReadRetryDelayMs = 75;

    /// <summary>
    /// Reads and deserializes an existing profile file, retrying a bounded number of times on an
    /// <see cref="IOException"/> before letting one propagate to <see cref="LoadAsync"/>'s catch block.
    /// </summary>
    /// <remarks>
    /// A SHARING VIOLATION IS NOT "THE FILE IS MISSING OR CORRUPT" (RemEx-8y3qy), and treating it as
    /// one was the actual clobber. <see cref="LoadAsync"/>'s catch block cannot tell those apart - any
    /// exception, including a momentary lock held by another reader or writer (the auto-snapshot, a
    /// savefile import, antivirus), made it substitute an all-default <see cref="DashboardProfile"/>
    /// for <see cref="CurrentProfile"/>. That default then looked like the user's real profile to
    /// every read-modify-write save in the app - a tutorial dismissal, a reduced-motion toggle, a
    /// telemetry-driven sensor restore - and the very next one of those persisted it over the real
    /// customization inside the 2-second debounce. Retrying here means a lock that clears within a
    /// couple of hundred milliseconds, which is the ordinary case for the sharing violations above,
    /// never reaches that catch block at all.
    /// <para>
    /// ONLY <see cref="IOException"/> RETRIES. A malformed-JSON read throws <see cref="JsonException"/>,
    /// which is not transient - retrying it wastes the same quarter-second three times over and still
    /// fails, so it falls straight through to the existing corrupt-file rename instead.
    /// </para>
    /// </remarks>
    /// <param name="onAttemptFailed">
    /// TEST SEAM ONLY (RemEx-8y3qy round 2). Invoked synchronously with the attempt number right
    /// after that attempt hits an <see cref="IOException"/>, before the retry delay. Production never
    /// passes one; a test can use it to release a deliberately-held lock at the exact moment the first
    /// attempt is known to have failed, which makes the contention AND its resolution deterministic
    /// instead of racing a fixed delay against the retry loop.
    /// </param>
    internal static async Task<DashboardProfile?> ReadExistingProfileAsync(
        string filePath, Action<int>? onAttemptFailed = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<DashboardProfile>(json, JsonOptions);
            }
            catch (IOException) when (attempt < TransientReadAttempts)
            {
                onAttemptFailed?.Invoke(attempt);
                await Task.Delay(TransientReadRetryDelayMs);
            }
        }
    }

    /// <inheritdoc />
    public Task<DashboardProfile> LoadAsync() => LoadAsyncCore(onReadAttemptFailed: null);

    /// <summary>
    /// TEST SEAM ONLY (RemEx-8y3qy round 2). Runs the real <see cref="LoadAsync"/> path with
    /// <see cref="ReadExistingProfileAsync"/>'s attempt-observed callback wired through, so a test can
    /// release a deliberately-held lock at an exact, known point in the retry loop instead of racing a
    /// fixed delay against it.
    /// </summary>
    internal Task<DashboardProfile> LoadAsyncForTests(Action<int> onReadAttemptFailed) =>
        LoadAsyncCore(onReadAttemptFailed);

    private async Task<DashboardProfile> LoadAsyncCore(Action<int>? onReadAttemptFailed)
    {
        await _gate.WaitAsync();
        try
        {
            DashboardProfile profile;
            MigrationOutcome outcome;

            ProfileFileMissingOnLoad = !File.Exists(_filePath);
            if (ProfileFileMissingOnLoad)
            {
                profile = MigrateProfile(new DashboardProfile(), out outcome)!;
            }
            else
            {
                // MIGRATED BEFORE THE THEME SERVICE SEES IT, not after (RemEx-dbkzy), and through
                // the shared reader so this is not a second opinion about what a file on disk means.
                // RETRIED ON A TRANSIENT I/O FAILURE, not just attempted once (RemEx-8y3qy) - see
                // ReadExistingProfileAsync for why.
                profile = MigrateProfile(
                    await ReadExistingProfileAsync(_filePath, onReadAttemptFailed) ?? new DashboardProfile(),
                    out outcome)!;
            }

            // ONCE PER LOAD, HERE, RATHER THAN ON EVERY APPLY. ThemeService warns about an unusable
            // seed each time it repaints, which is every drag of every slider; this occurrence is
            // the one that carries information. The write-back below is what makes it once per
            // INSTALL rather than once per launch.
            if (outcome.Warning is not null)
            {
                Debug.WriteLine($"[RemexLayout] Customization migrated with repairs: {outcome.Warning}");
                Trace.TraceWarning($"DashboardLayoutService.LoadAsync: customization migrated with repairs - {outcome.Warning}");
            }

            // Apply persisted theme settings to the UI.
            _themeService.ApplyCustomization(profile.Customization);

            CurrentProfile = profile;

            // A LOAD THAT JUST SUCCEEDED MEANS CurrentProfile IS TRUSTWORTHY AGAIN (RemEx-8y3qy
            // round 2). Cleared before the possible RequestSave below - a migrating profile arriving
            // right after a prior failed load must not have its own write-back blocked by that
            // failure's flag. LoadFailureWarning is stale-state of the same shape: leaving a prior
            // failure's message in place after a load that just succeeded would tell the user their
            // layout could not be loaded when it plainly just was.
            _profileIsFallback = false;
            LoadFailureWarning = null;

            // A MIGRATION THAT IS NEVER WRITTEN BACK IS NOT A MIGRATION, IT IS A RE-DERIVATION
            // (review finding). Nothing else persists the stamp except the Palette Studio's save, so
            // a user who never opens that screen would re-run the legacy arm on every launch: the
            // repaired seed would stay corrupt on disk, the warning above would log forever, and the
            // values they end up with would follow whatever the preset catalogue says TODAY rather
            // than what it said when they upgraded.
            //
            // RequestSave only arms a debounce timer, so it does not re-enter the gate this method
            // is holding - and the guard means the ordinary launch, where nothing changed, writes
            // nothing.
            //
            // NOT ON A FRESH INSTALL, and that exclusion is a review finding rather than an
            // optimisation. SchemaVersion defaults to 0, so a brand-new DashboardProfile always
            // takes the schema-0 arm and reports Changed - meaning first launch would write a
            // profile with nothing in it to preserve. That write raises ProfileSaved, which arms the
            // savefile service's snapshot debounce, which 30 seconds later autosnapshots the empty
            // default AND prunes to the newest five - deleting a real backup from the previous
            // install while the restore prompt is still open. Skipping the stamp here costs one
            // no-op migration on the next launch and nothing else.
            if (outcome.Changed && !ProfileFileMissingOnLoad) RequestSave(profile);

            return profile;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemexLayout] Failed to load profile from '{_filePath}': {ex.Message}");

            // ONLY MALFORMED CONTENT GETS QUARANTINED (RemEx-8y3qy round 2). Every exception used to
            // rename the file to .bak, but an IOException or UnauthorizedAccessException says nothing
            // about what is IN the file - only that this read could not get a clean look at it.
            // Renaming a file this method never actually examined out from under its own user is a
            // second, unrelated data loss on top of whatever caused the read to fail. A JsonException
            // is different: those bytes WERE read successfully and do not parse, so quarantining them
            // for diagnostics while falling back is still the right call.
            var existed = !ProfileFileMissingOnLoad;
            if (existed && ex is JsonException)
            {
                try
                {
                    var backupPath = _filePath + ".bak";
                    File.Move(_filePath, backupPath, overwrite: true);
                    Debug.WriteLine($"[RemexLayout] Corrupt profile renamed to '{backupPath}'");
                }
                catch (Exception moveEx)
                {
                    Debug.WriteLine($"[RemexLayout] Could not rename corrupt profile: {moveEx.Message}");
                }
            }

            LoadFailureWarning = $"Dashboard layout could not be loaded ({ex.GetType().Name}). Defaults have been applied.";

            // Migrated even though it is brand new, so the fresh profile is stamped current and the
            // next save does not read as schema 0. A default profile migrates to itself; what would
            // not survive is skipping the stamp, because the migration would then re-run against a
            // record that had already been written by this build.
            var profile = MigrateProfile(new DashboardProfile(), out _)!;
            _themeService.ApplyCustomization(profile.Customization);
            CurrentProfile = profile;

            // A FABRICATED PROFILE MUST NOT LOOK SAVE-WORTHY (RemEx-8y3qy round 2). `existed` is
            // exactly "this was not a fresh install": a brand-new user has no real profile to protect
            // and still needs their first save to work, but anyone else just had their real
            // customization replaced with defaults in memory only - RequestSave and SaveInternalAsync
            // must refuse to write that over the file until a load actually succeeds.
            _profileIsFallback = existed;

            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(DashboardProfile profile)
    {
        // A DIRECT SAVE SUPERSEDES A QUEUED ONE, BY DEFINITION - and until this line it did not.
        // SaveInternalAsync wrote the file and left _pendingProfile armed, so a debounced write
        // queued up to two seconds earlier would fire afterwards and put the older profile back.
        // The reachable case is the savefile import (RemexSavefileService.ImportDashboardLayoutAsync
        // calls this directly): the import lands, the stale timer fires, and the restore silently
        // reverts on disk with nothing logged. Pre-existing - a card drag racing an import has the
        // same shape - but RemEx-dbkzy's load-time write-back arms that timer on every launch of an
        // unmigrated profile, which turns a rare race into a routine one.
        CancelPendingSave();

        // NO GENERATION: a direct save is the caller's explicit instruction and always writes. Only
        // the debounced path can be superseded, because only it represents an intention the user has
        // already moved on from.
        return SaveInternalAsync(profile, generation: null);
    }

    /// <summary>
    /// Drops any queued debounced write. Held under <see cref="_saveQueueLock"/> because the timer
    /// callback and the caller's thread both touch these two fields.
    /// </summary>
    private void CancelPendingSave()
    {
        lock (_saveQueueLock)
        {
            _saveGeneration++;
            _pendingProfile = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    /// <summary>
    /// Debounced save — queues a write that fires after <see cref="DebounceMs"/>
    /// of inactivity. Safe to call on every card move/resize.
    /// </summary>
    public void RequestSave(DashboardProfile profile)
    {
        // A FALLBACK PROFILE MUST NEVER BE THE BASE OF A SAVE (RemEx-8y3qy round 2). Every caller
        // here built `profile` as `CurrentProfile with { ... }`; while _profileIsFallback is set,
        // CurrentProfile is a fabricated default rather than what the user actually saved, and
        // queueing it would persist that default over the real file once the debounce fires. See the
        // field's doc comment for the loss this trades away.
        if (_profileIsFallback)
        {
            Trace.TraceWarning(
                "DashboardLayoutService.RequestSave: refusing to queue a save - the loaded profile is a fallback default, not the user's real one");
            return;
        }

        CurrentProfile = profile;

        lock (_saveQueueLock)
        {
            _saveGeneration++;
            _pendingProfile = profile;
            _debounceTimer?.Dispose();
            _debounceTimer = ArmDebounce();
        }
    }

    private Timer ArmDebounce() =>
        new Timer(
            // A timer callback cannot await, and a dropped fault here means the debounced save
            // never happened and nothing said so - the user loses the layout they just edited.
            _ => FlushAsync().FireAndForget("flush the debounced dashboard layout save"),
            null,
            DebounceMs,
            Timeout.Infinite);

    /// <summary>
    /// Forces any pending debounced write to disk immediately.
    /// Call on application shutdown.
    /// </summary>
    public async Task FlushAsync()
    {
        DashboardProfile? profile;
        long generation;
        lock (_saveQueueLock)
        {
            // TAKEN AND CLEARED UNDER THE SAME LOCK. Read-then-clear outside one lets a SaveAsync
            // land between the two and be overwritten by the very profile it superseded.
            profile = _pendingProfile;
            generation = _saveGeneration;
            _pendingProfile = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (profile is null) return;

        await SaveInternalAsync(profile, generation);
    }

    /// <summary>
    /// Whether a captured save generation has been overtaken. <c>null</c> is a direct save, which is
    /// never superseded.
    /// </summary>
    /// <remarks>
    /// EXTRACTED SO IT IS NOT WHOLLY UNTESTED. The race it guards - a debounced write preempted
    /// between dequeue and gate acquisition, then overtaken by a direct save - cannot be reproduced
    /// deterministically through the public API without a timing dependency, which is a flake rather
    /// than a test. Splitting the decision out means the LOGIC is pinned by assertion and only the
    /// one-line placement rests on review; the placement is what the source guard in
    /// DashboardLayoutSaveOrderingTests covers.
    /// </remarks>
    internal static bool IsSuperseded(long? captured, long current) =>
        captured is { } c && current != c;

    /// <summary>
    /// Writes a profile to <paramref name="filePath"/> without ever exposing a reader to a partial
    /// file (RemEx-8y3qy round 2).
    /// </summary>
    /// <remarks>
    /// SERIALIZING STRAIGHT INTO THE DESTINATION — the previous behaviour — opens it with
    /// <see cref="FileShare.Read"/>, so a concurrent reader (another instance's <see cref="LoadAsync"/>,
    /// or <c>App.axaml.cs</c>'s own <c>ReadAndMigrate</c>) can observe a truncated write mid-flight and
    /// get a <see cref="JsonException"/> — which used to read as "corrupt" and quarantine a perfectly
    /// good profile (the very failure mode <see cref="ReadExistingProfileAsync"/> exists to survive).
    /// Writing to a sibling temp file first and moving it into place is atomic on the same volume: a
    /// reader either sees the old complete file or the new complete one, never a partial one.
    /// </remarks>
    private static async Task WriteProfileAtomicallyAsync(string filePath, DashboardProfile profile)
    {
        var tempPath = filePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            // Best-effort: do not leave a partial .tmp behind for the NEXT save to trip a reader over.
            // The failure itself is the caller's to log and swallow, as it already does.
            try { File.Delete(tempPath); } catch { /* diagnostics only; the write failure is what matters */ }
            throw;
        }
    }

    /// <summary>Synchronous twin of <see cref="WriteProfileAtomicallyAsync"/>, for <see cref="Dispose"/> — which cannot await.</summary>
    private static void WriteProfileAtomically(string filePath, DashboardProfile profile)
    {
        var tempPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* diagnostics only; the write failure is what matters */ }
            throw;
        }
    }

    private async Task SaveInternalAsync(DashboardProfile profile, long? generation)
    {
        // THE WAIT IS INSIDE THE TRY. It was outside, so an ObjectDisposedException from a gate
        // disposed while a debounced write was in flight bypassed the only diagnostic on this path
        // and surfaced at FireAndForget, which has no logger here and falls back to a
        // Debug.WriteLine that compiles out of Release. Silent, on the path that persists the
        // user's layout.
        var acquired = false;
        try
        {
            await _gate.WaitAsync();
            acquired = true;

            // A SECOND, DEFENSE-IN-DEPTH CHECK (RemEx-8y3qy round 2). RequestSave already refuses to
            // queue while _profileIsFallback is set, but a direct SaveAsync reaches here without going
            // through RequestSave at all, and a write that was ALREADY queued before the flag flipped
            // could still be sitting in _pendingProfile when it does. Either way, the gate is where
            // the answer is checked last, same as the supersede check just below it.
            if (_profileIsFallback)
            {
                Trace.TraceWarning(
                    "DashboardLayoutService.SaveInternalAsync: refusing to write - the loaded profile is a fallback default, not the user's real one");
                return;
            }

            // SUPERSEDED WHILE IT WAITED. Re-checked here rather than before the wait, because the
            // gate is where the ordering is actually decided - SemaphoreSlim does not release FIFO,
            // so two correctly sequenced waiters can still land out of order.
            if (IsSuperseded(generation, Volatile.Read(ref _saveGeneration))) return;

            await WriteProfileAtomicallyAsync(_filePath, profile);

            ProfileSaved?.Invoke();
        }
        catch (Exception ex)
        {
            // The Android guard that used to wrap this made it unreachable, so a failed save
            // has been reporting NOTHING. Deleting the guard and its body along with it would
            // have left a bare swallow here, which is worse: the user silently loses their
            // dashboard layout. Un-gated instead, so the diagnostic actually runs.
            //
            // Debug.WriteLine is a stopgap - it compiles out of Release. This service takes no
            // ILogger today, and giving it one is RemEx-t4tc's job (bare catch-swallow blocks),
            // not a dead-branch cleanup's. Recorded there rather than quietly widened here.
            System.Diagnostics.Debug.WriteLine($"[RemexPersistence] ERROR saving profile: {ex.Message}");
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    public void Dispose()
    {
        // UNDER THE LOCK, LIKE EVERY OTHER READER OF THESE FIELDS. Unguarded, Dispose could read
        // _debounceTimer during the window where RequestSave has disposed the old timer and not yet
        // assigned the new one - disposing nothing, and leaving a live timer armed on a disposed
        // service to fault against a disposed gate two seconds later.
        DashboardProfile? dropped;
        lock (_saveQueueLock)
        {
            _saveGeneration++;
            dropped = _pendingProfile;
            _pendingProfile = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        // A DROPPED QUEUE IS A LOST EDIT, and since the migration write-back it can be the schema
        // stamp itself - closing the app within the debounce window of a migrating launch would
        // silently re-migrate on the next one, contradicting the "once per install" claim in
        // LoadAsync. Dispose cannot await, so this is a synchronous best-effort write rather than a
        // flush: the callers that matter (CanvasDashboardViewModel, SettingsViewModel) already
        // FlushAsync before shutdown, and this is the backstop for the ones that do not.
        if (dropped is not null)
        {
            try
            {
                // ATOMIC HERE TOO (RemEx-8y3qy round 2) - see WriteProfileAtomically. A reader racing
                // process shutdown deserves the same guarantee a normal debounced save gives it.
                WriteProfileAtomically(_filePath, dropped);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RemexPersistence] ERROR draining the save queue on dispose: {ex.Message}");
            }
        }

        _gate.Dispose();
    }
}
