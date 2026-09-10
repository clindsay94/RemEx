using Avalonia.Controls;

namespace Remex.Desktop.Controls;

public partial class DashboardBackgroundControl : ContentControl
{
    public DashboardBackgroundControl()
    {
        InitializeComponent();
    }

    // No hand-written InitializeComponent: a parameterless one shadows the generated
    // InitializeComponent(bool loadXaml = true) override, so AvaloniaXamlLoader.Load(this) never
    // runs and every x:Name field stays null — that exact NullReferenceException, in
    // ConfirmationDialog's TitleText field, is what froze RemEx on every destructive action
    // (RemEx-wdqx; ConfirmationDialog.axaml.cs itself is gone, deleted on this branch). Removed
    // here for consistency rather than to fix a throw: this file was never exposed. Its only
    // x:Name (GradientAnimated) sits inside the ControlTemplate, a separate namescope, so the
    // generator emits no field for it at all — a name inside a template is not a name on the
    // control.
}
