using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Services.Readiness;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>One row of the System status card.</summary>
/// <remarks>
/// Holds the KEYS as well as the resolved text. The keys are what the tests assert against, because
/// asserting on resolved English would pass just as happily against a row that resolved to the wrong
/// sentence in the other eight languages.
/// </remarks>
public sealed partial class SystemStatusRowViewModel : ObservableObject
{
    public SystemStatusRowViewModel(ReadinessCheck check)
    {
        Id = check.Id;
        State = check.State;
        TitleKey = SystemStatusPresentation.TitleKey(check.Id);
        SentenceKey = SystemStatusPresentation.SentenceKey(check.Id, check.State);
        Affordance = SystemStatusPresentation.AffordanceFor(check.Id);
        ShowsAffordance = SystemStatusPresentation.ShowsAffordance(check);
        Resolve();
    }

    public ReadinessCheckId Id { get; }
    public ReadinessState State { get; }
    public string TitleKey { get; }
    public string SentenceKey { get; }
    public SystemStatusAffordance Affordance { get; }
    public bool ShowsAffordance { get; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _sentence = string.Empty;

    /// <summary>True for the states that need the user's eye, which is every state except Ok.</summary>
    public bool NeedsAttention => State != ReadinessState.Ok;

    /// <summary>Red dot. Something is definitely stopping the host working.</summary>
    public bool IsProblem => State == ReadinessState.Problem;

    /// <summary>
    /// Amber dot: a warning, or a check that could not run.
    /// </summary>
    /// <remarks>
    /// UNKNOWN IS AMBER, NOT GREY OR GREEN. A check that could not run is the one thing nothing else
    /// will report, so rendering it as neutral would hide exactly what the card exists to surface -
    /// the same reasoning that stops IsFullyReady counting it as passing.
    /// </remarks>
    public bool IsAttention => State is ReadinessState.Warning or ReadinessState.Unknown;

    public bool ShowsFix => ShowsAffordance && Affordance == SystemStatusAffordance.Fix;

    /// <summary>Whether this row offers the explain button (RemEx-tb0a).</summary>
    public bool ShowsExplain => ShowsAffordance && Affordance == SystemStatusAffordance.Explain;

    /// <summary>Resource key for this row's help text.</summary>
    public string HelpBodyKey => SystemStatusPresentation.HelpBodyKeyFor(Id);

    /// <summary>Re-reads both strings in the current language.</summary>
    public void Resolve()
    {
        Title = LocalizationService.Instance[TitleKey];
        Sentence = LocalizationService.Instance[SentenceKey];
    }
}

/// <summary>
/// The System status card: what the host needs in order to work, and what is not working (RemEx-id37).
/// </summary>
/// <remarks>
/// <para>
/// **COLLAPSED WHEN EVERYTHING IS READY**, because a card that is always open trains people to stop
/// reading it. It collapses on <see cref="SystemReadinessReport.IsFullyReady"/> specifically, which is
/// true only when every applicable row is Ok — deliberately not "nothing is a Problem", since that
/// would fold Warning and Unknown into the green state and hide them.
/// </para>
/// <para>
/// **NOTHING HERE REPAIRS ANYTHING ON ITS OWN.** Every fix is a button the user presses. The refresh
/// runs the checks and reports; it never changes the machine to make its own report look better.
/// </para>
/// </remarks>
public sealed partial class SystemStatusViewModel : ObservableObject, IDisposable
{
    private readonly Func<ISystemReadinessService?> _resolveService;
    private readonly Func<Func<SystemReadinessReport?>, Task<SystemReadinessReport?>> _runOffUiThread;
    private readonly Func<IStartupRegistrationService?> _resolveStartup;
    private readonly Func<Action, Task> _runRepairOffUiThread;
    private bool _disposed;

    /// <summary>Production constructor: resolve the host service, run the probe on the thread pool.</summary>
    public SystemStatusViewModel()
        : this(ResolveFromHost, work => Task.Run(work), ResolveStartupFromApp, work => Task.Run(work))
    {
    }

    /// <summary>Test seam. Neither dependency can be exercised in a unit test as it stands.</summary>
    internal SystemStatusViewModel(
        Func<ISystemReadinessService?> resolveService,
        Func<Func<SystemReadinessReport?>, Task<SystemReadinessReport?>> runOffUiThread,
        Func<IStartupRegistrationService?>? resolveStartup = null,
        Func<Action, Task>? runRepairOffUiThread = null)
    {
        _resolveService = resolveService;
        _runOffUiThread = runOffUiThread;
        _resolveStartup = resolveStartup ?? ResolveStartupFromApp;
        _runRepairOffUiThread = runRepairOffUiThread ?? (work => Task.Run(work));
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    public ObservableCollection<SystemStatusRowViewModel> Rows { get; } = [];

    /// <summary>Every applicable row is Ok, so the card shows one line and stays shut.</summary>
    [ObservableProperty] private bool _isFullyReady;

    /// <summary>True once a report has come back, so the card renders nothing before that.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRendered))]
    private bool _hasReport;

    /// <summary>
    /// The card is on screen once it has something to say — a report, or that it could not get one.
    /// </summary>
    /// <remarks>
    /// NOT just <see cref="HasReport"/>. Review found the card bound to that alone, so when the host
    /// was unreachable it silently VANISHED — on the one surface a user opens precisely to find out
    /// that the host is down. Saying nothing at all is only marginally better than saying everything
    /// is fine.
    /// </remarks>
    public bool IsRendered => HasReport || IsUnavailable;

    [ObservableProperty] private bool _isChecking;

    /// <summary>
    /// True when the host is not reachable, so the card can say nothing rather than guess.
    /// </summary>
    /// <remarks>
    /// The host can genuinely be absent — <c>EmbeddedHostServiceLocator</c> documents the degraded
    /// client-only mode. Rendering an empty green card there would be the worst outcome available:
    /// it would state that everything is fine using no information at all.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRendered))]
    private bool _isUnavailable;

    /// <summary>Runs every check and rebuilds the rows.</summary>
    /// <remarks>
    /// **OFF THE UI THREAD, BECAUSE <see cref="ISystemReadinessService.Run"/> LAUNCHES A PROCESS.**
    /// The firewall check shells out and blocks; both the implementation and the interface say so.
    /// Called on the UI thread it would freeze the window for as long as the probe takes.
    /// </remarks>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var report = await _runOffUiThread(() => _resolveService()?.Run());

            // EVERYTHING BELOW TOUCHES BOUND STATE, and mutating a bound ObservableCollection off the
            // UI thread is a real crash in Avalonia. Today the continuation lands on the UI thread
            // because every caller starts there, but that is an invariant nothing checks - one
            // future eager-init on the thread pool and Rows.Clear() runs off-thread with no compile
            // or test signal. Asserted rather than assumed.
            Debug.Assert(
                Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess(),
                "the readiness continuation must resume on the UI thread before touching Rows");

            IsUnavailable = report is null;
            if (report is null)
            {
                Rows.Clear();
                HasReport = false;
                return;
            }

            Rows.Clear();
            foreach (var check in SystemStatusPresentation.WorstFirst(report.Applicable))
            {
                Rows.Add(new SystemStatusRowViewModel(check));
            }

            IsFullyReady = report.IsFullyReady;
            HasReport = true;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Performs the one repair this card offers: register RemEx to start at sign-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **USER-INVOKED, NEVER AUTOMATIC.** Nothing on this card repairs anything on its own — the
    /// refresh reports and does not touch the machine. This runs only because somebody pressed the
    /// button on the row it belongs to.
    /// </para>
    /// <para>
    /// It re-checks afterwards rather than assuming it worked, so the row reflects what the machine
    /// now says rather than what the click intended. A repair that silently failed but repainted the
    /// row green would be worse than no button at all.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Supplied by the view to show the help dialog; null when nothing has wired one (RemEx-tb0a).
    /// </summary>
    /// <remarks>
    /// Same shape as the confirmation delegate established by RemEx-07jx, and wired from the same
    /// host, because this IS that dialog used informationally - one button, nothing destructive. A
    /// view that cannot host a dialog leaves this null and the command does nothing, which is the
    /// right failure for a button whose only job is to tell you something.
    /// </remarks>
    public Func<string, string, string, Task<bool>>? OnExplainRequested { get; set; }

    /// <summary>Explains one row's state and what to do about it (RemEx-tb0a).</summary>
    /// <remarks>
    /// The text is looked up by the row's key rather than passed in, so the dialog cannot show one
    /// check's advice against another's title - which is the failure a shared dialog invites.
    /// </remarks>
    [RelayCommand]
    private async Task ExplainAsync(SystemStatusRowViewModel? row)
    {
        if (row is null || !row.ShowsExplain || OnExplainRequested is null)
        {
            return;
        }

        await OnExplainRequested(
            LocalizationService.Instance[row.TitleKey],
            LocalizationService.Instance[row.HelpBodyKey],
            LocalizationService.Instance["SystemStatus_HelpClose"]);
    }

    [RelayCommand]
    private async Task FixAsync(SystemStatusRowViewModel? row)
    {
        if (row is null || !row.ShowsFix || row.Id != ReadinessCheckId.Autostart)
        {
            return;
        }

        var startup = _resolveStartup();
        if (startup is null || !startup.IsSupported)
        {
            return;
        }

        try
        {
            await _runRepairOffUiThread(() => startup.SetEnabled(true));
        }
        catch (Exception ex)
        {
            // CATCHES EVERYTHING ON PURPOSE, and review is why the list is gone. It named three
            // types, and StartupRegistrationService calls WindowsIdentity.GetCurrent().Name OUTSIDE
            // its own try - so on a domain-joined PC with the domain controller unreachable, the
            // SID lookup throws IdentityNotMappedException, which matched none of them. That escapes
            // Task.Run, is rethrown at the await, and AsyncRelayCommand reposts it to the UI context
            // as unhandled: RemEx dies on a button press. Path.GetTempPath can do the same with
            // SecurityException.
            //
            // The re-check below is what reports the failure honestly - the row comes back amber and
            // says so - so filtering by type bought nothing and risked the process.
            Debug.WriteLine($"[Remex] Autostart repair failed: {ex}");
        }

        await RefreshAsync();
    }

    private static ISystemReadinessService? ResolveFromHost()
    {
        // NOT EmbeddedHostServiceLocator.Require<T>, which throws when the host is absent. A status
        // card is exactly the surface that must survive the host being down - it is the thing the
        // user would open TO FIND OUT that the host is down.
        try
        {
            return App.EmbeddedHostServices?.GetService(typeof(ISystemReadinessService))
                as ISystemReadinessService;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static IStartupRegistrationService? ResolveStartupFromApp() =>
        App.Services?.GetService(typeof(IStartupRegistrationService)) as IStartupRegistrationService;

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var row in Rows)
        {
            row.Resolve();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
    }
}
