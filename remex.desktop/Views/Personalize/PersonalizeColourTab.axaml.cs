using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Remex.Desktop.ViewModels;
using System;

namespace Remex.Desktop.Views.Personalize;

public partial class PersonalizeColourTab : UserControl
{
    public PersonalizeColourTab()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The seed wheel has finished an interaction — a drag released, or an arrow key let go — so the
    /// colour the user landed on joins the recently-used row.
    /// </summary>
    /// <remarks>
    /// IN CODE-BEHIND BECAUSE IT IS AN EVENT, NOT A COMMAND (moved verbatim off
    /// PersonalizationPanelView.axaml.cs, RemEx-4kv0g.4.3, with the wheel itself). The distinction
    /// the recents list needs is "the drag ended", which no bindable property carries: every colour
    /// a drag passes through raises the same change notification as the one it stops on, so binding
    /// to the seed would fill the row with eight colours nobody chose.
    /// </remarks>
    private void OnSeedCommitted(object? sender, EventArgs e)
    {
        (DataContext as CustomizationViewModel)?.CommitSeedToRecents();
    }

    /// <summary>
    /// The Contrast slider's detent (RemEx-4kv0g.16): a value within 0.05 of -1, 0 or 1 moves the
    /// thumb onto it, and the TwoWay binding then carries that value to the view model.
    /// </summary>
    /// <remarks>
    /// IN THE VIEW AS WELL AS THE VIEW MODEL, AND FOR A MEASURED REASON. The view model snaps a
    /// direct write too, but a source that changes itself while Avalonia's binding is writing to it
    /// is not pushed back to the target: the headless render test saw the view model at 0 and the
    /// thumb still at 0.03. Snapping the slider's own value here is what moves the thumb. Both call
    /// <see cref="CustomizationViewModel.SnapContrastToDetent"/>, so there is one rule. A drag is not
    /// trapped by it: Slider recomputes each move from the pointer's absolute position.
    /// </remarks>
    private void OnContrastValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider) return;
        var snapped = CustomizationViewModel.SnapContrastToDetent(e.NewValue);
        if (snapped != e.NewValue) slider.SetCurrentValue(RangeBase.ValueProperty, snapped);
    }
}
