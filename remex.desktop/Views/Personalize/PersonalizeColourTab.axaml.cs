using Avalonia.Controls;
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
}
