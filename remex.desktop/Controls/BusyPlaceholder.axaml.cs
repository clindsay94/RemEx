using Avalonia;
using Avalonia.Controls;

namespace Remex.Desktop.Controls;

/// <summary>
/// Which shape the shared busy affordance takes (RemEx-kjdi). Two modes, one control, so a third
/// hand-rolled "loading" widget never gets a reason to exist.
/// </summary>
public enum BusyPlaceholderMode
{
    /// <summary>An inline indeterminate ring next to dimmed content — for a command-shaped wait,
    /// typically a button's own content.</summary>
    Spinner,

    /// <summary>3-4 shimmering rounded bars standing in for a list's first fill.</summary>
    Skeleton,
}

/// <summary>
/// The one reusable busy/loading affordance for the whole app (RemEx-kjdi §2). Before this, the
/// only <c>IsIndeterminate</c> ProgressBar in the app was <c>PairingDialog.axaml:45</c> and every
/// other wait — log fetch, trust refresh, the process list's first fill — gave no feedback at all.
/// </summary>
/// <remarks>
/// Wraps arbitrary <see cref="ContentControl.Content"/> so it can sit either INSIDE a Button
/// (<see cref="BusyPlaceholderMode.Spinner"/>, wrapping the button's own label) or AROUND a list
/// (<see cref="BusyPlaceholderMode.Skeleton"/>, wrapping the real <c>ItemsControl</c>/<c>ListBox</c>
/// so it can hide it under the skeleton rows while <see cref="IsBusy"/> is true). One control, one
/// template (<c>BusyPlaceholder.axaml</c>), Classes select the visual — no per-view style
/// duplication.
/// <para>
/// <see cref="IsReducedMotion"/> is a plain styled property rather than this control reading a
/// shared setting itself: RemEx-7kea's reduce-animations flag lives on <c>ShellViewModel</c>, and
/// not every view-model that owns a wait (e.g. <c>TaskManagerViewModel</c>) held a reference to the
/// shell before this bead. Each call site binds its own forwarding property; the control only
/// needs to know the answer, not where it comes from.
/// </para>
/// </remarks>
public partial class BusyPlaceholder : ContentControl
{
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<BusyPlaceholder, bool>(nameof(IsBusy));

    public static readonly StyledProperty<BusyPlaceholderMode> ModeProperty =
        AvaloniaProperty.Register<BusyPlaceholder, BusyPlaceholderMode>(nameof(Mode), BusyPlaceholderMode.Spinner);

    public static readonly StyledProperty<bool> IsReducedMotionProperty =
        AvaloniaProperty.Register<BusyPlaceholder, bool>(nameof(IsReducedMotion));

    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public BusyPlaceholderMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public bool IsReducedMotion
    {
        get => GetValue(IsReducedMotionProperty);
        set => SetValue(IsReducedMotionProperty, value);
    }

    public BusyPlaceholder()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    // Plain Classes, NOT pseudo-classes: a pseudo-class selector matching this control BY ITS OWN
    // TYPE NAME, declared inside that same type's own template file, does not reliably match here
    // (verified live via ui-hotreload — IsBusy="True" produced no visible change at all). Named
    // Classes toggled on the instance and matched with a plain ".busy.skeleton"-style selector is
    // the pattern DashboardBackgroundControl.axaml already uses successfully in this codebase
    // (Classes.aurora-animated, Classes.palette-transition-suppressed) — proven to work, so this
    // follows it instead of re-attempting the pseudo-class route.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsBusyProperty ||
            change.Property == ModeProperty ||
            change.Property == IsReducedMotionProperty)
        {
            UpdateVisualState();
        }
    }

    private void UpdateVisualState()
    {
        SetClass("busy", IsBusy);
        SetClass("skeleton", Mode == BusyPlaceholderMode.Skeleton);
        SetClass("reduced-motion", IsReducedMotion);
    }

    private void SetClass(string name, bool value)
    {
        if (value)
        {
            if (!Classes.Contains(name)) Classes.Add(name);
        }
        else
        {
            Classes.Remove(name);
        }
    }
}
