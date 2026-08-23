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
    /// Guards <see cref="_debounceTimer"/> and <see cref="_pendingProfile"/>, which are written from
    /// the caller's thread and from the timer callback's.
    /// </summary>
    private readonly object _saveQueueLock = new();
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

    /// <inheritdoc />
    public async Task<DashboardProfile> LoadAsync()
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
                var json = await File.ReadAllTextAsync(_filePath);
                profile = MigrateProfile(
                    JsonSerializer.Deserialize<DashboardProfile>(json, JsonOptions) ?? new DashboardProfile(),
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

            // Rename the corrupt file so it isn't loaded again, but is preserved for diagnostics.
            if (File.Exists(_filePath))
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
        return SaveInternalAsync(profile);
    }

    /// <summary>
    /// Drops any queued debounced write. Held under <see cref="_saveQueueLock"/> because the timer
    /// callback and the caller's thread both touch these two fields.
    /// </summary>
    private void CancelPendingSave()
    {
        lock (_saveQueueLock)
        {
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
        CurrentProfile = profile;

        lock (_saveQueueLock)
        {
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
        lock (_saveQueueLock)
        {
            // TAKEN AND CLEARED UNDER THE SAME LOCK. Read-then-clear outside one lets a SaveAsync
            // land between the two and be overwritten by the very profile it superseded.
            profile = _pendingProfile;
            _pendingProfile = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (profile is null) return;

        await SaveInternalAsync(profile);
    }

    private async Task SaveInternalAsync(DashboardProfile profile)
    {
        await _gate.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);

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
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        _gate.Dispose();
    }
}
