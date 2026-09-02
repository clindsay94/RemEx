using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Whether a PHONE is attached, as every status indicator on the PC should say it (RemEx-7zzw).
/// </summary>
/// <remarks>
/// <para>
/// EXTRACTED FROM <see cref="ShellViewModel"/> BECAUSE THERE ARE FIVE INDICATORS, NOT ONE. RemEx-0z7w
/// put this state on the shell and rebound the shell's dot, which left Settings, Canvas and Home
/// still showing <c>Connection.IsConnected</c> — so with no phone paired the app contradicted itself
/// screen to screen, and a user reads that as a bug in the new indicator rather than a stale one in
/// the old three. Uniformly wrong was at least coherent.
/// </para>
/// <para>
/// A PROCESS-WIDE SINGLETON, matching <c>LocalizationService.Instance</c> and
/// <c>ActivityService.Instance</c>. The alternative was threading a <c>ShellViewModel</c> reference
/// into two more view models for a fact that is not shell-specific — which couples them to the shell
/// to learn something about the host, and would have to be undone the next time a fourth surface
/// wants it. Every consumer exposes a one-line <c>Presence</c> property instead.
/// </para>
/// <para>
/// The judgement about what counts as a phone is NOT here. It is stated once in
/// <see cref="PhonePresence"/> with its own tests (RemEx-porg), because re-deriving it per call site
/// is exactly how those five indicators came to disagree.
/// </para>
/// <para>
/// DELIBERATELY NOT DISPOSABLE, AND WITH NO TEAR-DOWN AT ALL. A first version implemented
/// <c>IDisposable</c>, which on a shared singleton is a kill switch anybody can pull: a
/// <c>using var</c>, a container registration or a tidy-up in a shutdown path would stop the only
/// poll in the process and freeze every status dot, with no way to restart it and nothing to log. A
/// second version kept an <c>internal StopForTests</c> — the same one-way switch with a smaller
/// blast radius and no callers, which would have frozen the singleton for every later test in the
/// assembly the moment somebody used it (review). There is nothing to release that outliving the
/// process would leak: the timer is rooted by the dispatcher either way.
/// </para>
/// </remarks>
public sealed partial class PhonePresenceMonitor : ObservableObject
{
    /// <summary>The one instance every indicator reads.</summary>
    public static PhonePresenceMonitor Instance { get; } = new();

    /// <summary>Whether at least one PHONE is attached — not whether the loopback link is up.</summary>
    [ObservableProperty]
    private bool _isPhoneAttached;

    /// <summary>What an indicator should say about attached phones, already localized.</summary>
    [ObservableProperty]
    private string _presenceText = string.Empty;

    /// <summary>
    /// The accessible name for a presence dot: TWO STATES, and it names the control.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="PresenceText"/> ON PURPOSE (RemEx-x12a, re-applied in review of
    /// RemEx-7zzw). A dot's accessible name must say what the control IS plus its state, and must not
    /// change minute to minute — a first pass bound these dots to the presence message itself, which
    /// never identifies the element, varies with the device name and count, and on the shell was
    /// announced twice because it is byte-identical to the label beside it.
    /// </remarks>
    [ObservableProperty]
    private string _presenceAccessibleName = string.Empty;

    /// <summary>
    /// The refinement <see cref="IsPhoneAttached"/> cannot express: host-down and no-phone both
    /// collapse to <c>false</c> there, which is the exact defect RemEx-44gc6 exists to fix in the
    /// collapsed drawer. ADDITIVE — <see cref="IsPhoneAttached"/> keeps its existing meaning for the
    /// four other indicators RemEx-7zzw already lined up; a test pins that the two can never
    /// contradict each other.
    /// </summary>
    [ObservableProperty]
    private ShellConnectionState _state;

    /// <summary>The single attached phone's name, mirroring <see cref="PhonePresenceStatus.FirstDeviceName"/>.</summary>
    [ObservableProperty]
    private string? _deviceName;

    /// <summary>The single attached phone's address, mirroring <see cref="PhonePresenceStatus.RemoteAddress"/>.</summary>
    [ObservableProperty]
    private string? _remoteAddress;

    /// <summary>
    /// What the collapsed drawer's <c>ToolTip.Tip</c> shows — the one channel that survives when the
    /// text column is hidden (RemEx-44gc6). Starts from the same localized <see cref="PresenceText"/>
    /// every other surface already shows, with the address appended when there is one to show.
    /// </summary>
    [ObservableProperty]
    private string _summaryTooltip = string.Empty;

    private DispatcherTimer? _timer;

    /// <summary>Whether the embedded host is not registered — a PC fault, not an absent phone.</summary>
    public bool IsHostDown => State == ShellConnectionState.HostDown;

    /// <summary>Whether the host is healthy and no phone is attached.</summary>
    public bool HasNoPhone => State == ShellConnectionState.NoPhone;

    /// <summary>Whether at least one phone is attached — the <see cref="State"/>-flavoured twin of
    /// <see cref="IsPhoneAttached"/>, for the flyout's action matrix.</summary>
    public bool HasPhone => State == ShellConnectionState.PhoneAttached;

    /// <summary>
    /// CommunityToolkit.Mvvm generated partial hook — fired whenever <see cref="State"/> changes, so
    /// the three computed booleans above stay in sync without a caller having to remember to raise
    /// them.
    /// </summary>
    partial void OnStateChanged(ShellConnectionState value)
    {
        OnPropertyChanged(nameof(IsHostDown));
        OnPropertyChanged(nameof(HasNoPhone));
        OnPropertyChanged(nameof(HasPhone));
    }

    private PhonePresenceMonitor()
    {
        // The text is an already-localized snapshot, so a language switch has to recompute it or
        // every indicator keeps the previous language (RemEx-q3h0's pattern).
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

        // GUARDED, because this runs inside a static initializer (review). A throw here becomes a
        // TypeInitializationException and the CLR then refuses to initialise this type for the rest
        // of the process — so every status dot in the app would throw from inside its binding,
        // forever, with no restart path. The old arrangement failed once, loudly, in a constructor.
        // The realistic trigger is a translator adding a placeholder the format call has no argument
        // for; the first tick will retry in three seconds.
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Presence",
                "The first phone-presence read failed; the poll will retry", ex);
        }

        StartPolling();
    }

    /// <summary>
    /// Re-reads the host's live sessions and republishes what the indicators should say.
    /// </summary>
    /// <remarks>
    /// THE SESSION SOURCE IS RESOLVED ON EVERY CALL, not cached. The embedded host publishes its
    /// container after it starts and this singleton is constructed on first use, which can be
    /// earlier — a cached null would then stick for the process (the mistake found in review of
    /// RemEx-n8xk, on the same two-container arrangement).
    /// <para>
    /// POLLED RATHER THAN PUSHED, AND A PUSH IS THE BETTER END STATE. There IS a channel — the host
    /// is this process, and <c>PingPongHandler</c> already reaches into <c>ActivityService.Instance</c>
    /// across that boundary. The reasons a poll is right today all expire: there is no disconnect
    /// signal yet (RemEx-2xjv), the connect activity is throttled through a CompareExchange so it is
    /// not a one-to-one presence edge, and a registry event would fire on a host thread and need
    /// marshalling. <c>Snapshot()</c> is a walk over a small concurrent dictionary.
    /// </para>
    /// </remarks>
    public void Refresh()
    {
        // The app container is tried first and is expected to miss — only the host registers this.
        // Kept for parity with how ConnectionViewModel resolves ICertificateService, and so a
        // desktop-side test double would win if one were ever registered.
        var source = App.Services?.GetService(typeof(IClientSessionSource)) as IClientSessionSource
            ?? App.EmbeddedHostServices?.GetService(typeof(IClientSessionSource)) as IClientSessionSource;

        // A MISSING SOURCE IS NOT AN ABSENT PHONE (RemEx-t6s1). The embedded host starts inside a
        // try/catch and can fail; when it does, nothing registers IClientSessionSource and every
        // reading below collapses to NoPhone — the same thing the shell says when the host is
        // perfectly healthy and no phone is paired. With the drawer COLLAPSED the two text lines are
        // hidden and the dot is the entire indicator, so the user goes and checks their phone, their
        // Wi-Fi and their pairing while the fault is on this PC. Before RemEx-0z7w the dot carried
        // the host-link fact, so in this one state the diagnostic was a strict downgrade.
        //
        // BRANCHED ON THE SOURCE ITSELF, not on the snapshot: Evaluate(null) cannot tell "no host
        // registered it" from "a host that has no sessions", and only the first is a PC fault.
        if (source is null)
        {
            IsPhoneAttached = false;
            State = ShellConnectionState.HostDown;
            DeviceName = null;
            RemoteAddress = null;

            // The same words to a screen reader as on the screen. Reusing the no-phone a11y string
            // would say "no phone" while the row said the host is down, which is the shape that
            // makes an indicator worse than none.
            PresenceText = LocalizationService.Instance["Shell_PhonePresenceHostDown"];
            PresenceAccessibleName = PresenceText;
            SummaryTooltip = PresenceText;
            return;
        }

        var status = PhonePresence.Evaluate(source.Snapshot());
        var (key, argument) = PhonePresence.Describe(status);
        var template = LocalizationService.Instance[key];

        var attached = status.State != PhonePresenceState.NoPhone;
        IsPhoneAttached = attached;
        State = attached ? ShellConnectionState.PhoneAttached : ShellConnectionState.NoPhone;
        DeviceName = status.FirstDeviceName;
        RemoteAddress = status.RemoteAddress;
        PresenceAccessibleName = LocalizationService.Instance[
            attached ? "A11y_PhonePresenceAttached" : "A11y_PhonePresenceNone"];
        PresenceText = argument is null
            ? template
            : string.Format(CultureInfo.CurrentCulture, template, argument);

        // RemoteAddress is null whenever there is nothing worth appending — no phone, several
        // phones, or a single phone Evaluate could not resolve an address for — so the tooltip
        // degrades to the plain presence line rather than a format string with a hole in it.
        SummaryTooltip = RemoteAddress is null
            ? PresenceText
            : string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Instance["Shell_StatusTooltipAddressLine"],
                PresenceText,
                RemoteAddress);
    }

    private void StartPolling()
    {
        // NO DISPATCHER YET: skip, and SAY SO (review). This is a one-way door on a process-wide
        // singleton — nothing restarts the poll — and the ordering constraint it depends on got
        // weaker with the extraction: the trigger used to be ShellViewModel's constructor, which the
        // app builds after startup, and is now whoever reads Instance first. Unreachable in the
        // shipping app, where AppBuilder assigns Application.Current before any view model exists;
        // logged because if it ever does happen, every dot in the app freezes on one reading with
        // nothing to explain it.
        if (Avalonia.Application.Current is null)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Presence",
                "No Avalonia application yet, so phone presence will not refresh for this process", null);
            return;
        }

        // THE CREATION THREAD IS NO LONGER PINNED BY A CONSTRUCTOR (review). This used to run in
        // ShellViewModel's ctor, which the app builds on the UI thread; it now runs in a lazy static
        // initializer triggered by whoever reads Instance first. Every reader today is a compiled
        // binding, so it holds — but the first off-thread caller would otherwise either throw out of
        // a type initializer or, worse, get a timer bound to the wrong context that never ticks,
        // freezing every dot in the app for the process lifetime. Posting is cheap and removes the
        // constraint rather than documenting it.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(StartPolling);
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

}
