using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Routines;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// The routine countdown window's state (routines spec §8.6): the routine, the action, who started it,
/// the seconds left, and one command, Cancel.
/// </summary>
/// <remarks>
/// The seconds shown are display only. The authoritative 15 s belongs to the agent's
/// <c>RoutineCountdownCoordinator</c>, which closes this window when it ends; a window that ticked
/// slow or not at all must never delay or extend the real countdown.
/// </remarks>
public sealed partial class RoutineCountdownViewModel : ObservableObject
{
    private readonly RoutineCountdownPrompt _prompt;
    private Action? _onCancel;

    /// <summary>Creates the view model. <paramref name="onCancel"/> runs at most once.</summary>
    public RoutineCountdownViewModel(RoutineCountdownPrompt prompt, Action onCancel)
    {
        _prompt = prompt;
        _onCancel = onCancel;
        _secondsLeft = Math.Max(0, prompt.Seconds);

        RoutineName = prompt.RoutineName;
        ActionLabel = RoutineStrings.ActionLabel(prompt.Verb);
        OriginText = RoutineStrings.Format(
            "Routine_Countdown_From",
            string.IsNullOrWhiteSpace(prompt.PhoneName)
                ? LocalizationService.Instance["Routine_Countdown_PhoneUnknown"]
                : prompt.PhoneName,
            RoutineStrings.SourceLabel(prompt.Source));

        // A test and a dry run both mean "nothing will be turned off", and the person at the PC must
        // be able to tell that from the window alone. Test wins when both hold: it is the narrower
        // statement, and the verb is not even logged as issued.
        NoteText = prompt.TestRun
            ? RoutineStrings.Format("Routine_Countdown_TestNote", ActionLabel)
            : prompt.DryRun
                ? LocalizationService.Instance["Routine_Countdown_DryRunNote"]
                : null;
    }

    /// <summary>The run this window counts down for.</summary>
    public string RunId => _prompt.RunId;

    /// <summary>The routine's name.</summary>
    public string RoutineName { get; }

    /// <summary>The localized action label, e.g. "Shut down".</summary>
    public string ActionLabel { get; }

    /// <summary>"From {phone}, {source}".</summary>
    public string OriginText { get; }

    /// <summary>The test-run or dry-run note, or null for a real run.</summary>
    public string? NoteText { get; }

    /// <summary>Whether <see cref="NoteText"/> is shown.</summary>
    public bool HasNote => NoteText is not null;

    /// <summary>Seconds left, as displayed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownText))]
    private int _secondsLeft;

    /// <summary>"Shut down in 12 s".</summary>
    public string CountdownText => RoutineStrings.Format("Routine_Countdown_Seconds", ActionLabel, SecondsLeft);

    /// <summary>Advances the displayed countdown by one second, stopping at zero.</summary>
    public void Tick()
    {
        if (SecondsLeft > 0)
        {
            SecondsLeft--;
        }
    }

    /// <summary>The one button. Default and cancel button of the window (Enter, Space, Esc).</summary>
    [RelayCommand]
    private void Cancel()
    {
        var cancel = Interlocked.Exchange(ref _onCancel, null);
        cancel?.Invoke();
    }
}
