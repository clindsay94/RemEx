using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// A profile with nothing to migrate: stamped straight at <see cref="CustomizationMigration.CurrentSchemaVersion"/>
    /// rather than run through <see cref="MigrateProfile"/>.
    /// </summary>
    /// <remarks>
    /// NEVER PASS A FABRICATED <c>new DashboardProfile()</c> THROUGH <see cref="MigrateProfile"/>
    /// (RemEx-8twk0.1 review). <c>Migrate</c> exists to translate an OLD FILE's values forward; its
    /// schema-3 arm unconditionally sets <c>ColorSource</c> to <c>Custom</c> for anything it touches,
    /// because a value already on disk was chosen by hand or by a preset. A profile that never had a
    /// file has nothing to translate - the schema-0 record default IS the spec's fresh-install answer
    /// (<c>WindowsAccent</c>) - so running it through the arm anyway silently reassigned every new
    /// user's colour source and made the Windows-accent-follow feature unreachable from a clean
    /// install. Used for every "there is no real profile" case: file missing, a read that returned
    /// null without throwing, and the exception fallback.
    /// </remarks>
    private static DashboardProfile FreshProfile() =>
        new() { Customization = new CustomizationSettings { SchemaVersion = CustomizationMigration.CurrentSchemaVersion } };

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
    /// GUARDS <see cref="RequestSave"/>, THE DEBOUNCED CALL INTO <see cref="SaveInternalAsync"/>, AND
    /// <see cref="Dispose"/>'s drain of a still-queued write - not <see cref="SaveAsync"/>
    /// (RemEx-8y3qy round 3 and 4). Every read-modify-write save reachable
    /// through <see cref="RequestSave"/> (<c>ShellViewModel.CompleteTutorial</c>/
    /// <c>OnIsReducedMotionChanged</c>, <c>CanvasDashboardViewModel.TriggerSave</c>/
    /// <c>DismissCoachMark</c>) builds its new profile from <see cref="CurrentProfile"/> with no way
    /// to tell "this is the real, freshly-loaded profile" from "this is the fallback default" — so
    /// while this flag is set, any of them would persist the fabricated default over whatever the
    /// user actually had saved. Refusing the write here costs the user only the single change they
    /// just made in THIS session; letting it through would have cost them the entire saved profile.
    /// <see cref="SaveAsync"/> is the opposite shape: its caller always hands in a complete, real
    /// profile it already has from somewhere else (the only caller today is a savefile import), so
    /// there is nothing fabricated to protect against - and refusing it would silently break that
    /// exact recovery path, which round 2 of this bead did (RemExSavefileService.
    /// ImportDashboardLayoutAsync calls SaveAsync then LoadAsync; a swallowed SaveAsync let the
    /// following LoadAsync succeed, clear this flag, and report the import successful while the STALE
    /// file sat there untouched). <see cref="SaveAsync"/> clears this flag itself before writing.
    /// <para>
    /// The flag also clears the moment a load actually succeeds, so ordinary saving resumes as soon
    /// as the file is readable again.
    /// </para>
    /// </remarks>
    private volatile bool _profileIsFallback;

    /// <summary>Test seam: the current value of <see cref="_profileIsFallback"/>.</summary>
    internal bool ProfileIsFallbackForTests => _profileIsFallback;

    /// <summary>
    /// True once <see cref="LoadAsync"/> or <see cref="ReloadAsync"/> has completed at least once -
    /// success or the failure fallback, either way (RemEx-71b1m). Before that, <see cref="CurrentProfile"/>
    /// is only this class's own constructor default: a bare <c>new DashboardProfile()</c>, unmigrated
    /// and stamped at SchemaVersion 0, that no read of any file ever produced. <see cref="_profileIsFallback"/>
    /// alone does not cover this gap - it defaults to false, the exact value it also holds once a REAL
    /// load has succeeded, so a save that races ahead of the very first load reads as "nothing wrong"
    /// and persists that raw default over whatever the user's file actually holds. Observed concretely
    /// as a first-run boot writing a schema-0 customization to disk before the real load's migrated
    /// record ever landed.
    /// </summary>
    private volatile bool _hasLoadedOnce;

    /// <summary>Test seam: the current value of <see cref="_hasLoadedOnce"/>.</summary>
    internal bool HasLoadedOnceForTests => _hasLoadedOnce;

    private readonly ILogger<DashboardLayoutService> _logger;

    /// <summary>Raised after a profile is successfully written to disk by <see cref="SaveInternalAsync"/> (i.e. after <see cref="SaveAsync"/> or a flushed <see cref="RequestSave"/>).</summary>
    public event Action? ProfileSaved;

    /// <summary>
    /// Raised when <see cref="CurrentProfile"/> is replaced with a profile a caller did not build
    /// FROM the previous one — a <see cref="ReloadAsync"/> off disk, whether that succeeds or falls
    /// back to defaults (RemEx-waqb4). A savefile import is the concrete path:
    /// <c>RemexSavefileService.ImportDashboardLayoutAsync</c> calls <see cref="SaveAsync"/> then
    /// <see cref="ReloadAsync"/>, and it is that trailing <see cref="ReloadAsync"/> that actually swaps
    /// <see cref="CurrentProfile"/> for the imported values and raises this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONLY <see cref="ReloadAsync"/> CAN RAISE THIS (review, HIGH) — <see cref="LoadAsyncCore"/>
    /// gates both of its <see cref="CurrentProfile"/> assignments on the <c>isReplacement</c> flag,
    /// and <see cref="LoadAsync"/> always passes <c>false</c>. The first version raised this from
    /// EVERY load, including <see cref="LoadAsync"/>'s: <c>RemexSavefileService.BuildSavefileAsync</c>
    /// calls it on every manual export and every 30-second autosnapshot timer tick — a background,
    /// off-UI-thread read nobody asked to replace anything — and <c>CanvasDashboardViewModel</c> calls
    /// it on its own periodic refreshes. Either one firing this 30 seconds after an unrelated slider
    /// nudge reset a bound, open Personalize sheet out from under the user, from a thread pool thread.
    /// </para>
    /// <para>
    /// <see cref="RequestSave"/>'s write is excluded for a different reason — see its own call site's
    /// comment: it hands in <c>CurrentProfile with { ... }</c>, the SAME profile a caller (typically
    /// a cached view model) just built, not a foreign one. A subscriber that wants "did the load
    /// change what disk holds" has <see cref="ProfileSaved"/> for that.
    /// </para>
    /// </remarks>
    public event Action? ProfileReplaced;

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

    public DashboardLayoutService(ThemeService themeService, ILogger<DashboardLayoutService>? logger = null)
        : this(DefaultFilePath, themeService, logger)
    {
    }

    /// <summary>
    /// TEST SEAM ONLY (RemEx-8y3qy). Every real caller goes through the public constructor above,
    /// which always resolves <see cref="DefaultFilePath"/> - this overload changes nothing about
    /// production behaviour, it only lets a test point an instance at a file OTHER instances in the
    /// same test assembly are not also touching.
    /// </summary>
    /// <remarks>
    /// THE SHARED REDIRECTED FILE IS A REAL CROSS-TEST HAZARD, not a hypothetical one.
    /// <c>build/TestHostStateRedirect.cs</c> redirects <see cref="RemexDataPaths.PerUserDirectory"/>
    /// once per ASSEMBLY, not once per test, so every <see cref="DashboardLayoutService"/> the whole
    /// test run constructs through the public constructor shares one
    /// <c>dashboard_layout.json</c>. A test that arms a debounce timer (directly, or indirectly
    /// through a ViewModel) and does not flush or dispose before returning can have that timer fire
    /// during a LATER, unrelated test and write over whatever that test just wrote or is about to
    /// read. <c>DashboardLayoutClobberTests</c> depends on nothing else touching its file at all
    /// while it runs, so it uses this overload with its own private temp directory instead of
    /// fighting for exclusive use of the one every other test in the assembly shares.
    /// </remarks>
    internal DashboardLayoutService(string filePath, ThemeService themeService, ILogger<DashboardLayoutService>? logger = null)
    {
        _themeService = themeService;
        _logger = logger ?? NullLogger<DashboardLayoutService>.Instance;

        _filePath = filePath;

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    /// <summary>
    /// How many times a transient I/O failure retries — reading an existing profile, and moving a
    /// freshly-written one into place (RemEx-8y3qy round 3: the move can hit a sharing violation
    /// exactly the way the read can, since a reader holds no delete-share and neither operation is
    /// unique to one direction).
    /// </summary>
    private const int TransientIoAttempts = 3;

    /// <summary>Delay between transient-I/O retries, read or write.</summary>
    private const int TransientIoRetryDelayMs = 75;

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
            catch (IOException) when (attempt < TransientIoAttempts)
            {
                onAttemptFailed?.Invoke(attempt);
                await Task.Delay(TransientIoRetryDelayMs);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A PLAIN READ, NOT A REPLACEMENT (RemEx-waqb4 review, HIGH). This is what
    /// <c>RemexSavefileService.BuildSavefileAsync</c> calls on every manual export AND every 30-second
    /// autosnapshot, and what <c>CanvasDashboardViewModel</c> calls on its own periodic refreshes — none
    /// of those intend to replace anything a view model has cached, so this overload never raises
    /// <see cref="ProfileReplaced"/>. <see cref="ReloadAsync"/> is the one that does.
    /// </remarks>
    public Task<DashboardProfile> LoadAsync() => LoadAsyncCore(onReadAttemptFailed: null, isReplacement: false);

    /// <summary>
    /// Loads the persisted profile and raises <see cref="ProfileReplaced"/> once the swap into
    /// <see cref="CurrentProfile"/> completes (RemEx-waqb4 review, HIGH). Use this only from a caller
    /// that actually intends to replace the live profile out from under whatever cached it: the
    /// initial app load (<c>App.InitializeAppAsync</c>) and a savefile import's post-<see cref="SaveAsync"/>
    /// re-read (<c>RemexSavefileService.ImportDashboardLayoutAsync</c>). Every other reader wants
    /// <see cref="LoadAsync"/> instead - see its remarks for why raising this from every read broke a
    /// bound Personalize sheet from a background thread 30 seconds after an unrelated slider nudge.
    /// </summary>
    public Task<DashboardProfile> ReloadAsync() => LoadAsyncCore(onReadAttemptFailed: null, isReplacement: true);

    /// <summary>
    /// TEST SEAM ONLY (RemEx-8y3qy round 2). Runs the real <see cref="LoadAsync"/> path with
    /// <see cref="ReadExistingProfileAsync"/>'s attempt-observed callback wired through, so a test can
    /// release a deliberately-held lock at an exact, known point in the retry loop instead of racing a
    /// fixed delay against it.
    /// </summary>
    internal Task<DashboardProfile> LoadAsyncForTests(Action<int> onReadAttemptFailed) =>
        LoadAsyncCore(onReadAttemptFailed, isReplacement: false);

    private async Task<DashboardProfile> LoadAsyncCore(Action<int>? onReadAttemptFailed, bool isReplacement)
    {
        await _gate.WaitAsync();
        try
        {
            DashboardProfile profile;
            MigrationOutcome outcome;

            // ORPHANED TEMP FILES ARE SWEPT ONCE PER LOAD (RemEx-8y3qy round 5, LOW finding). See
            // SweepStaleTempFiles for why they can exist at all and why sweeping here is safe.
            SweepStaleTempFiles();

            ProfileFileMissingOnLoad = !File.Exists(_filePath);
            if (ProfileFileMissingOnLoad)
            {
                // A FRESH INSTALL IS STAMPED, NOT MIGRATED (RemEx-8twk0.1 review) - see FreshProfile.
                // Still routed through MigrateProfile: FreshProfile() is already at
                // CurrentSchemaVersion, so Migrate's own early-return makes this a true no-op while
                // keeping every ApplyCustomization call in this method behind an actual migration call.
                profile = MigrateProfile(FreshProfile(), out outcome)!;
            }
            else
            {
                // MIGRATED BEFORE THE THEME SERVICE SEES IT, not after (RemEx-dbkzy), and through
                // the shared reader so this is not a second opinion about what a file on disk means.
                // RETRIED ON A TRANSIENT I/O FAILURE, not just attempted once (RemEx-8y3qy) - see
                // ReadExistingProfileAsync for why.
                var existing = await ReadExistingProfileAsync(_filePath, onReadAttemptFailed);

                // The file existed but read back as null (e.g. a literal "null" on disk) - there is
                // still nothing real to migrate, so this is the same case as a missing file: stamp
                // rather than translate (RemEx-8twk0.1 review).
                profile = existing is null
                    ? MigrateProfile(FreshProfile(), out outcome)!
                    : MigrateProfile(existing, out outcome)!;
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

            // SET BEFORE ProfileReplaced RAISES (RemEx-71b1m, review LOW). A subscriber that reacts
            // to the replacement by saving must not find _hasLoadedOnce still false - that would
            // refuse its write for the very same reason a pre-load save must be refused, except this
            // subscriber is reacting to a load that has, in fact, just happened.
            _hasLoadedOnce = true;

            // A REAL REPLACEMENT, NOT A SAVE-THROUGH OR AN ORDINARY READ (RemEx-waqb4, tightened by
            // review) - see ProfileReplaced's own remarks for why RequestSave must never raise this,
            // and LoadAsync's for why a plain read must not either. Only isReplacement callers
            // (ReloadAsync) reach this. A view model cached over the old CurrentProfile
            // (CustomizationViewModel, held by ShellViewModel's ??=) has to hear about a REAL
            // replacement specifically, or the next slider nudge writes its stale snapshot back over
            // whatever this load just brought in.
            if (isReplacement) ProfileReplaced?.Invoke();

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

            // Stamped current rather than translated (RemEx-8twk0.1 review) - see FreshProfile - so
            // the next save does not read as schema 0 and does not silently reassign ColorSource away
            // from the spec's fresh-install default. Still routed through MigrateProfile: FreshProfile()
            // is already current, so this is a genuine no-op that also keeps the ApplyCustomization
            // below behind an actual migration call.
            var profile = MigrateProfile(FreshProfile(), out _)!;
            _themeService.ApplyCustomization(profile.Customization);
            CurrentProfile = profile;

            // SET BEFORE ProfileReplaced RAISES, SAME AS THE SUCCESS PATH ABOVE (RemEx-71b1m, review
            // LOW). A load attempt happened here too, even though it failed - a subscriber reacting
            // to the replacement must not find _hasLoadedOnce still false.
            _hasLoadedOnce = true;

            // Also gated on isReplacement (RemEx-waqb4), same reasoning as the success path above - a
            // fallback default is just as foreign to a view model cached over the profile this load
            // failed to preserve, but only when the caller actually asked to replace one.
            if (isReplacement) ProfileReplaced?.Invoke();

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
    public async Task SaveAsync(DashboardProfile profile)
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

        // AN EXPLICIT SAVE CARRIES A REAL PROFILE, ALWAYS (RemEx-8y3qy round 3, HIGH finding). This
        // caller did not build `profile` from CurrentProfile, so there is nothing fabricated in it to
        // protect against - and refusing it would silently break the one path that reaches here today:
        // RemexSavefileService.ImportDashboardLayoutAsync calls SaveAsync then LoadAsync. Round 2 left
        // the fallback guard covering this method too, so a swallowed SaveAsync let the following
        // LoadAsync succeed, clear the flag, and report the import successful while the file on disk
        // was still whatever was there before - the exact silent failure this bead exists to kill.
        // Cleared here, before the write, rather than left for LoadAsync to clear afterwards: nothing
        // between this line and the following LoadAsync should be able to read a stale "still failed"
        // state as true.
        //
        // CAPTURED FIRST AND RESTORED ON FAILURE (RemEx-8y3qy round 5, HIGH finding). Round 4 made a
        // failed write rethrow instead of being swallowed - but this clear ran unconditionally before
        // that write even started, so a save that then failed left the flag cleared against a
        // CurrentProfile that was NEVER actually confirmed good. Scenario: a startup load fails on a
        // locked-but-intact file (flag true); the user imports a savefile to recover; that import's own
        // write also cannot land (a longer-lived lock, a full disk) and throws - correctly, per round
        // 4 - but with the flag left cleared, the very next unrelated RequestSave (the next card drag)
        // would sail past its own guard and persist the still-fabricated CurrentProfile over the real
        // file. Restoring both in the catch means a failed recovery attempt leaves exactly the
        // protection it found, not less of it.
        var wasFallback = _profileIsFallback;
        var previousWarning = LoadFailureWarning;

        _profileIsFallback = false;
        LoadFailureWarning = null;

        try
        {
            // NO GENERATION: a direct save is the caller's explicit instruction and always writes.
            // Only the debounced path can be superseded, because only it represents an intention the
            // user has already moved on from.
            await SaveInternalAsync(profile, generation: null);
        }
        catch
        {
            // THE WRITE DID NOT LAND, so nothing about CurrentProfile's trustworthiness changed -
            // SaveInternalAsync never assigns it, and a rethrown exception here (round 4's own fix)
            // means the file did not change either. Restoring the captured state leaves the next
            // unrelated save exactly as guarded as it was before this call started.
            _profileIsFallback = wasFallback;
            LoadFailureWarning = previousWarning;
            throw;
        }
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
            _logger.LogWarning(
                "DashboardLayoutService: refusing to queue a save - the loaded profile is a fallback default, not the user's real one");
            return;
        }

        CurrentProfile = profile;

        // NO LOAD HAS EVER HAPPENED YET (RemEx-71b1m). Every caller here builds `profile` as
        // `CurrentProfile with { ... }` (TriggerSave, CompleteTutorial, DismissCoachMark,
        // OnIsReducedMotionChanged); before the first LoadAsync/ReloadAsync completes, that base is
        // still this class's own unmigrated, schema-0 constructor default - not anything a file on
        // disk ever produced. CurrentProfile is still updated above, same as ever, so an in-memory
        // reader sees the edit the caller just made (the pending real load is about to replace it
        // anyway, the same way it always replaces the constructor default) - but there is nothing
        // real on disk yet for this edit to be saved OVER, so queuing a WRITE here is exactly how a
        // first-run boot ended up with a schema-0 customization in dashboard_layout.json before the
        // real load's migrated record ever landed.
        if (!_hasLoadedOnce)
        {
            _logger.LogWarning(
                "DashboardLayoutService: not queuing a write - no profile has been loaded from disk yet, so there is nothing real to save over");
            return;
        }

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
    /// Whether an exception from <see cref="File.Move(string, string, bool)"/> looks like the kind of
    /// contention <see cref="WriteProfileAtomicallyAsync"/>/<see cref="WriteProfileAtomically"/> retry,
    /// rather than a genuine failure worth giving up on immediately.
    /// </summary>
    /// <remarks>
    /// BOTH EXCEPTION TYPES, MEASURED RATHER THAN GUESSED (round 4 finding). The natural assumption is
    /// that a locked destination throws <see cref="IOException"/>, the same as a locked read - but a
    /// <c>Move</c> onto a destination held open without <see cref="FileShare.Delete"/> throws
    /// <see cref="UnauthorizedAccessException"/> on Windows instead (<c>MoveFileEx</c> maps
    /// <c>ERROR_ACCESS_DENIED</c> to it), confirmed by <c>SaveAsync_SurfacesAMoveFailureToItsCaller</c>
    /// reproducing it deterministically. Retrying only <see cref="IOException"/>, as the read does,
    /// would have meant this exact contention shape never actually retried at all.
    /// </remarks>
    private static bool IsTransientMoveFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Deletes orphaned per-call temp files a crash left behind mid-write (RemEx-8y3qy round 5, LOW
    /// finding).
    /// </summary>
    /// <remarks>
    /// THE GUID IN THE TEMP NAME (round 4) MEANS NOTHING BUT A CRASH LEAVES ONE BEHIND. Both
    /// <see cref="WriteProfileAtomicallyAsync"/> and <see cref="WriteProfileAtomically"/> already
    /// delete their own temp file on any failure they observe - the gap is the process dying between
    /// creating it and reaching that cleanup, or before either runs at all, which nothing else ever
    /// revisits. Swept once per load rather than once per process so a leftover from a PREVIOUS run of
    /// the app is caught too, not only ones from earlier in this one.
    /// <para>
    /// GLOB-SCOPED, DELIBERATELY, AND NOTHING ELSE. Matches only "&lt;profile file name&gt;.*.tmp" in
    /// the profile's own directory - never another file in that folder, and never anything outside it.
    /// A per-file failure (the ordinary shape: another instance's write is genuinely still in flight,
    /// using its own fresh GUID) is swallowed and logged at Debug rather than surfaced - sweeping old
    /// wreckage is a courtesy, not a load-time correctness requirement, and must never itself be a
    /// reason the app cannot start.
    /// </para>
    /// <para>
    /// "LEAVE IT ALONE ON FAILURE" IS A WINDOWS-SHAPED ASSUMPTION (round 6 review note). On Windows a
    /// delete against another instance's in-flight temp genuinely fails, which is why swallowing it
    /// here is safe. <c>File.Delete</c> on Linux unlinks the directory entry out from under an open
    /// handle without complaint - the other instance's own write keeps going, but its later
    /// <c>File.Move</c> onto that now-vanished path fails and logs. Rare (both writers would need the
    /// exact same GUID collision odds aside, or this sweep would need to run mid-write, which it only
    /// does at load time) and already surfaced through that instance's own Warning log, so left as a
    /// known platform difference rather than a reason to change the sweep's behaviour.
    /// </para>
    /// </remarks>
    private void SweepStaleTempFiles()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        string[] staleFiles;
        try
        {
            staleFiles = Directory.GetFiles(directory, Path.GetFileName(_filePath) + ".*.tmp");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemexLayout] Could not enumerate stale temp files in '{directory}': {ex.Message}");
            return;
        }

        foreach (var stale in staleFiles)
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception ex)
            {
                // Ordinary case: another instance's write is genuinely still in flight with its own
                // fresh GUID temp name - leave it alone rather than fail the load over housekeeping.
                Debug.WriteLine($"[RemexLayout] Could not delete stale temp file '{stale}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes a profile to <paramref name="filePath"/> without ever exposing a reader to a partial or
    /// unflushed file (RemEx-8y3qy round 2 and 3).
    /// </summary>
    /// <remarks>
    /// SERIALIZING STRAIGHT INTO THE DESTINATION — the pre-round-2 behaviour — opens it with
    /// <see cref="FileShare.Read"/>, so a concurrent reader (another instance's <see cref="LoadAsync"/>,
    /// or <c>App.axaml.cs</c>'s own <c>ReadAndMigrate</c>) can observe a truncated write mid-flight and
    /// get a <see cref="JsonException"/> — which used to read as "corrupt" and quarantine a perfectly
    /// good profile (the very failure mode <see cref="ReadExistingProfileAsync"/> exists to survive).
    /// Writing to a sibling temp file first and moving it into place is atomic on the same volume: a
    /// reader either sees the old complete file or the new complete one, never a partial one.
    /// <para>
    /// THE MOVE ITSELF CAN FAIL WITH A SHARING VIOLATION (round 3 finding). <see cref="File.Move(string, string, bool)"/>
    /// needs Windows to grant delete access to the destination, and <see cref="ReadExistingProfileAsync"/>
    /// (via <c>File.ReadAllTextAsync</c>) opens it with <see cref="FileShare.Read"/> only
    /// — no <see cref="FileShare.Delete"/> — so a reader that is mid-read at the exact moment of the
    /// move can make the move itself throw (see <see cref="IsTransientMoveFailure"/> for which
    /// exception type that actually is). Retried the same way the read is, for the same reason: the
    /// contention is expected to clear within a couple of hundred milliseconds. A failure that survives
    /// every retry propagates to the caller (round 4, HIGH finding) rather than being swallowed - see
    /// <see cref="SaveInternalAsync"/>.
    /// </para>
    /// <para>
    /// FLUSHED TO DISK BEFORE THE MOVE, not merely to the OS write cache. <c>Stream.FlushAsync</c> only
    /// reaches the OS's own buffers - it was tried first and did not actually keep this promise
    /// (round 4 finding) - so both paths call the synchronous <c>Flush(flushToDisk: true)</c> instead,
    /// which is the only overload that asks the OS to commit to the physical device. The profile is a
    /// few KB; the synchronous call costs nothing worth avoiding. Without it, a move landing ahead of
    /// the write actually reaching disk would let a power loss surface a short file under the real name
    /// — exactly the corruption the temp-file-then-move was meant to rule out.
    /// </para>
    /// <para>
    /// THE TEMP NAME IS UNIQUE PER CALL, not a fixed <c>filePath + ".tmp"</c> (round 4 finding). Every
    /// instance of this service pointed at the same file used to share one temp path, so instance B's
    /// <see cref="FileMode.Create"/>/<see cref="FileShare.None"/> on that SAME name could hold it locked
    /// across the whole of instance A's retry window - a second, avoidable source of exactly the
    /// sharing-violation contention the retry above exists to survive. A GUID in the name means two
    /// concurrent writers, in this process or another, never touch the same temp file at all.
    /// </para>
    /// </remarks>
    private async Task WriteProfileAtomicallyAsync(string filePath, DashboardProfile profile)
    {
        var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(profile, JsonOptions);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await using (var writer = new StreamWriter(stream))
                {
                    await writer.WriteAsync(json);
                    await writer.FlushAsync();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, filePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransientMoveFailure(ex) && attempt < TransientIoAttempts)
            {
                await Task.Delay(TransientIoRetryDelayMs);
            }
            catch (Exception ex)
            {
                // Best-effort: do not leave a partial .tmp behind for the NEXT save to trip a reader
                // over. The write itself failed - that is what LogWarning below reports.
                await TryDeleteTempFileAsync(tempPath);
                _logger.LogWarning(ex,
                    "DashboardLayoutService: failed to save the dashboard profile to '{FilePath}' after {Attempts} attempt(s)",
                    filePath, attempt);
                throw;
            }
        }
    }

    /// <summary>Synchronous twin of <see cref="WriteProfileAtomicallyAsync"/>, for <see cref="Dispose"/> — which cannot await.</summary>
    private void WriteProfileAtomically(string filePath, DashboardProfile profile)
    {
        var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(profile, JsonOptions);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, filePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransientMoveFailure(ex) && attempt < TransientIoAttempts)
            {
                // Dispose cannot await. This runs once, at process shutdown, so a short blocking
                // sleep here costs nothing a flush wouldn't have cost anyway.
                Thread.Sleep(TransientIoRetryDelayMs);
            }
            catch (Exception ex)
            {
                TryDeleteTempFile(tempPath);
                _logger.LogWarning(ex,
                    "DashboardLayoutService: failed to drain the save queue to '{FilePath}' on Dispose after {Attempts} attempt(s)",
                    filePath, attempt);
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes a temp file left behind by a write that failed, retrying briefly on the same
    /// contention <see cref="IsTransientMoveFailure"/> already names (RemEx-8y3qy round 6, gate
    /// flake). This was a bare <c>try { File.Delete(tempPath); } catch { }</c> - fine for a genuine
    /// leftover, but a single missed attempt against a transient hold (an antivirus scan of a
    /// freshly-created file is the ordinary shape on Windows) left the temp behind with nothing
    /// logging why, which is exactly what made <c>SaveAsync_SurfacesAMoveFailureToItsCaller</c> flake
    /// once in the gate's full run while passing every time in isolation. A final failure logs at
    /// Debug rather than Warning - the write's OWN failure is already logged at Warning by the caller,
    /// and <see cref="SweepStaleTempFiles"/> is the backstop for whatever this could not clean up.
    /// </summary>
    private async Task TryDeleteTempFileAsync(string tempPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(tempPath);
                return;
            }
            catch (Exception ex) when (IsTransientMoveFailure(ex) && attempt < TransientIoAttempts)
            {
                await Task.Delay(TransientIoRetryDelayMs);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "DashboardLayoutService: could not delete temp file '{TempPath}' after {Attempts} attempt(s); the next load's sweep will retry",
                    tempPath, attempt);
                return;
            }
        }
    }

    /// <summary>Synchronous twin of <see cref="TryDeleteTempFileAsync"/>, for <see cref="Dispose"/> — which cannot await.</summary>
    private void TryDeleteTempFile(string tempPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(tempPath);
                return;
            }
            catch (Exception ex) when (IsTransientMoveFailure(ex) && attempt < TransientIoAttempts)
            {
                // Dispose cannot await - see WriteProfileAtomically's own retry for why a short
                // blocking sleep is acceptable here.
                Thread.Sleep(TransientIoRetryDelayMs);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "DashboardLayoutService: could not delete temp file '{TempPath}' after {Attempts} attempt(s); the next load's sweep will retry",
                    tempPath, attempt);
                return;
            }
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

            // A SECOND, DEFENSE-IN-DEPTH CHECK - THE DEBOUNCED PATH ONLY (RemEx-8y3qy round 3 narrowed
            // this from "every call"; round 2's version also blocked SaveAsync, which is the HIGH
            // finding fixed alongside this). `generation is not null` is exactly "this call came from
            // RequestSave/FlushAsync", the same signal IsSuperseded below already uses to distinguish
            // a debounced write from a direct one. RequestSave already refuses to queue while
            // _profileIsFallback is set, but a write that was ALREADY queued before the flag flipped
            // could still be sitting in _pendingProfile when the timer fires - the gate is where that
            // answer is checked last, same as the supersede check just below it. SaveAsync's direct
            // writes (generation: null) never reach this branch: they carry an explicit, real profile
            // and already clear the flag themselves before calling in.
            if (generation is not null && _profileIsFallback)
            {
                _logger.LogWarning(
                    "DashboardLayoutService: refusing a debounced write - the loaded profile is a fallback default, not the user's real one");
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
            // ERROR HERE, WARNING INSIDE WriteProfileAtomicallyAsync (RemEx-8y3qy round 3): that
            // method already logs the specific write failure at Warning before rethrowing, so this is
            // the backstop for anything else in this method that could throw (acquiring the gate,
            // reading _saveGeneration) - genuinely unexpected, hence the higher level, and still
            // logged rather than silently swallowed either way.
            _logger.LogError(ex, "DashboardLayoutService: error saving profile to '{FilePath}'", _filePath);

            // AN EXPLICIT SAVE MUST SURFACE ITS OWN FAILURE (RemEx-8y3qy round 4, HIGH finding).
            // generation is null only for SaveAsync's direct writes, and its only caller today -
            // RemexSavefileService.ImportDashboardLayoutAsync - awaits it, then immediately calls
            // LoadAsync and reports the import applied once LoadAsync returns. Swallowing the
            // exception here let that LoadAsync succeed against the UNCHANGED file and the import
            // report success while nothing had actually been written - the exact silent failure this
            // bead exists to kill, one hop further down than round 3 caught. The debounced path
            // (generation is not null) keeps swallowing on purpose: nobody awaits FlushAsync's result,
            // and the next card move or setting change requeues the same content anyway.
            if (generation is null) throw;
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
            // SAME GUARD AS RequestSave AND THE DEBOUNCED SaveInternalAsync CALL (RemEx-8y3qy round 4,
            // LOW finding). This drain was previously safe only because RequestSave refuses to queue
            // while _profileIsFallback is set, so `dropped` should never be fallback-derived - but that
            // was an invariant this method assumed rather than one it enforced, and the same queued-
            // before-the-flag-flipped race SaveInternalAsync guards against applies here too. Checked
            // explicitly so Dispose does not have to keep being the one caller that trusts it.
            if (_profileIsFallback)
            {
                _logger.LogWarning(
                    "DashboardLayoutService: refusing to drain a queued save on Dispose - the loaded profile is a fallback default, not the user's real one");
            }
            else
            {
                try
                {
                    // ATOMIC HERE TOO (RemEx-8y3qy round 2) - see WriteProfileAtomically. A reader
                    // racing process shutdown deserves the same guarantee a normal debounced save gives
                    // it.
                    WriteProfileAtomically(_filePath, dropped);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RemexPersistence] ERROR draining the save queue on dispose: {ex.Message}");
                }
            }
        }

        _gate.Dispose();
    }
}
