using Avalonia.Controls;

namespace Remex.Desktop.Controls;

public partial class DashboardBackgroundControl : ContentControl
{
    public DashboardBackgroundControl()
    {
        InitializeComponent();
    }

    // No hand-written InitializeComponent — see the note in ConfirmationDialog. Removed here for
    // consistency rather than to fix a throw: this file was never exposed. Its only x:Name
    // (GradientAnimated) sits inside the ControlTemplate, which is a separate namescope, so the
    // generator emits no field for it at all — the generated InitializeComponent for this type has
    // no FindNameScope block. A name inside a template is not a name on the control (RemEx-wdqx).
}
