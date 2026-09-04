using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Remex.Desktop.Views;

/// <summary>
/// Title + wrapped message + button row hosted as the
/// <see cref="Material.Dialog.CustomDialogBuilderParams.Content"/> of the dialogs
/// <see cref="MaterialDialogs.ConfirmAsync"/> and <see cref="MaterialDialogs.RestoreAsync"/> build.
/// Exists because Material.Avalonia.Dialogs' own <c>AlertDialog</c> (what those two builders used
/// before fix round 1) renders its <c>SupportingText</c> without <c>TextWrapping</c> set, which clips
/// rather than wraps a message longer than the dialog's width - moving both dialogs onto
/// <c>CreateCustomDialog</c> with this control as their content sidesteps that TextBlock entirely
/// instead of patching it. No x:Name'd control risk here (RemEx-wdqx does not apply): the generated
/// <c>InitializeComponent(bool loadXaml = true)</c> is not shadowed by a hand-written override.
/// </summary>
/// <remarks>
/// RemEx-x6a70.3 fix round 2: <see cref="CancelButton"/> and <see cref="ActionButton"/> are real RemEx
/// <c>Button</c>s living in this control's own content, not Material.Avalonia's <c>DialogButtons</c> -
/// see <see cref="MaterialDialogs"/>'s type remarks for why a library-rendered button cannot carry this
/// app's Classes vocabulary against 3.19.0. <see cref="ResultTask"/> is this control's own outcome,
/// resolved by whichever button was clicked and nothing else - <c>MaterialDialogs</c> must not rely on
/// <c>ShowDialog</c>'s own return value, because that reads a library-internal <c>DialogResult</c>
/// property external code has no way to set. A button click completes <see cref="ResultTask"/> and then
/// closes the window itself (<c>TopLevel.GetTopLevel(this)</c>); every other
/// dismissal (Escape, Alt+F4, the title-bar close button) leaves <see cref="ResultTask"/> incomplete,
/// which <see cref="MaterialDialogs.Resolve"/> treats as a decline.
/// </remarks>
public partial class DialogContent : UserControl
{
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes with the button that was clicked - <c>true</c> for <see cref="ActionButton"/>,
    /// <c>false</c> for <see cref="CancelButton"/>. Stays incomplete if the window closes any other way.
    /// </summary>
    public Task<bool> ResultTask => _tcs.Task;

    public DialogContent() : this(string.Empty, string.Empty, string.Empty, string.Empty, "primary") { }

    public DialogContent(string header, string message, string cancelText, string actionText, string actionClasses)
    {
        InitializeComponent();
        HeaderText.Text = header;
        MessageTextBlock.Text = message;
        CancelButton.Content = cancelText;
        ActionButton.Content = actionText;
        foreach (var cls in actionClasses.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            ActionButton.Classes.Add(cls);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Resolve(false);

    private void OnActionClick(object? sender, RoutedEventArgs e) => Resolve(true);

    private void Resolve(bool result)
    {
        _tcs.TrySetResult(result);
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }
}
