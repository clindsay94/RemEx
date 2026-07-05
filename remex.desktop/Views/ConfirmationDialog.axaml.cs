using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirmClicked(object? sender, RoutedEventArgs e) => Close(true);
}
