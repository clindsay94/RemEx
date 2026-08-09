using Avalonia.Controls;
using Avalonia.Interactivity;
using Remex.Desktop.Services;

namespace Remex.Desktop.Views;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog() : this("Preview Title", "Preview Message", "Confirm") { }

    public ConfirmationDialog(string title, string message, string confirmText)
    {
        InitializeComponent();

        // OS window title; the Localize markup extension does not apply to Window.Title, so set it here.
        Title = LocalizationService.Instance["Dialog_ConfirmTitle"];
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmBtn.Content = confirmText;
        CancelBtn.Content = LocalizationService.Instance["Btn_Cancel"];
    }

    /// <summary>
    /// Escape dismisses, routed to the same handler the Cancel button uses (RemEx-xxifk).
    /// </summary>
    /// <remarks>
    /// An override rather than a KeyBinding because this dialog's cancel is a code-behind handler
    /// and a KeyBinding can only reach a command. Enter is deliberately not handled: whether a
    /// default action is safe to bind is a per-dialog question that stays on RemEx-df08, while
    /// Escape carries none of that risk - cancelling is never the destructive option.
    /// </remarks>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            e.Handled = true;
            Close(false);
            return;
        }

        base.OnKeyDown(e);
    }


    // No hand-written InitializeComponent here, deliberately. Avalonia's name generator emits
    // `public void InitializeComponent(bool loadXaml = true)`, which loads the XAML AND assigns the
    // x:Name fields below it. A parameterless `private void InitializeComponent()` does not collide
    // with that — it WINS overload resolution against it, because a candidate applicable without
    // filling an optional parameter beats one that needs a default. The XAML then loads and every
    // named field stays null, so the constructor throws NullReferenceException on first use. It
    // compiles, it renders, and it fails at runtime, which is why it shipped (RemEx-wdqx).

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirmClicked(object? sender, RoutedEventArgs e) => Close(true);
}
