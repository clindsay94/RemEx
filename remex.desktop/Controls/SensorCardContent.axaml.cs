using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Controls;

/// <summary>
/// The sensor card's visual (RemEx-4kv0g.18.1, spec D1 §1), extracted out of CanvasView.axaml so
/// the canvas and the tray flyout (.18.2) host the same markup. See SensorCardContent.axaml's
/// header comment for why <see cref="Sensor"/> is a styled property assigned to the root Grid's
/// DataContext rather than this control's own DataContext — that indirection lets a host bind
/// <see cref="Sensor"/> alongside the five canvas-only properties below off the same element.
/// </summary>
public partial class SensorCardContent : UserControl
{
    public static readonly StyledProperty<SensorViewModel?> SensorProperty =
        AvaloniaProperty.Register<SensorCardContent, SensorViewModel?>(nameof(Sensor));

    public static readonly StyledProperty<bool> IsPinnedToHomeProperty =
        AvaloniaProperty.Register<SensorCardContent, bool>(nameof(IsPinnedToHome));

    public static readonly StyledProperty<bool> HasAlertProperty =
        AvaloniaProperty.Register<SensorCardContent, bool>(nameof(HasAlert));

    public static readonly StyledProperty<bool> IsAlertTrippedProperty =
        AvaloniaProperty.Register<SensorCardContent, bool>(nameof(IsAlertTripped));

    public static readonly StyledProperty<ICommand?> AcknowledgeAlertCommandProperty =
        AvaloniaProperty.Register<SensorCardContent, ICommand?>(nameof(AcknowledgeAlertCommand));

    public static readonly StyledProperty<string?> AlertTooltipProperty =
        AvaloniaProperty.Register<SensorCardContent, string?>(nameof(AlertTooltip));

    public SensorViewModel? Sensor
    {
        get => GetValue(SensorProperty);
        set => SetValue(SensorProperty, value);
    }

    public bool IsPinnedToHome
    {
        get => GetValue(IsPinnedToHomeProperty);
        set => SetValue(IsPinnedToHomeProperty, value);
    }

    public bool HasAlert
    {
        get => GetValue(HasAlertProperty);
        set => SetValue(HasAlertProperty, value);
    }

    public bool IsAlertTripped
    {
        get => GetValue(IsAlertTrippedProperty);
        set => SetValue(IsAlertTrippedProperty, value);
    }

    public ICommand? AcknowledgeAlertCommand
    {
        get => GetValue(AcknowledgeAlertCommandProperty);
        set => SetValue(AcknowledgeAlertCommandProperty, value);
    }

    public string? AlertTooltip
    {
        get => GetValue(AlertTooltipProperty);
        set => SetValue(AlertTooltipProperty, value);
    }

    public SensorCardContent()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // RootGrid, not this control, carries the sensor as its DataContext (see the .axaml header
        // comment) — everything inside the moved markup binds against it directly, with no
        // "Sensor." prefix, while the five properties above stay reachable off this element via
        // RelativeSource even after RootGrid's DataContext flips.
        if (change.Property == SensorProperty && RootGrid is not null)
        {
            RootGrid.DataContext = Sensor;
        }
    }
}
