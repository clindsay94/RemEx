using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Remex.Desktop.Services;

namespace Remex.Desktop.Views;

/// <summary>
/// First-run restore prompt: shown once, on desktop lifetimes only, when
/// <c>dashboard_layout.json</c> is missing on load but a rolling auto-snapshot exists. Mirrors
/// <see cref="ConfirmationDialog"/>'s minimal card dialog shape.
/// </summary>
public partial class RestorePromptWindow : Window
{
    public RestorePromptWindow() : this(DateTime.UtcNow) { }

    public RestorePromptWindow(DateTime snapshotTimestampUtc)
    {
        InitializeComponent();

        // OS window title; the Localize markup extension does not apply to Window.Title.
        Title = LocalizationService.Instance["Restore_PromptTitle"];
        MessageText.Text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["Restore_PromptMessage"],
            snapshotTimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    }

    // No hand-written InitializeComponent — see the note in ConfirmationDialog. A parameterless one
    // shadows the generated `InitializeComponent(bool loadXaml = true)` and leaves MessageText,
    // SkipBtn and RestoreBtn null (RemEx-wdqx). This file inherited the pattern by being copied from
    // that dialog, which is exactly how the defect spread.

    private void OnSkipClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnRestoreClicked(object? sender, RoutedEventArgs e) => Close(true);

    /// <summary>Shows the prompt owned by <paramref name="owner"/>, deriving the displayed date from <paramref name="snapshotPath"/>'s filename (falling back to its last-write time). Returns true if the user chose to restore.</summary>
    public static async Task<bool> ShowAsync(Window owner, string snapshotPath)
    {
        var timestamp = TryParseSnapshotTimestamp(snapshotPath) ?? SafeGetLastWriteTimeUtc(snapshotPath);
        var dialog = new RestorePromptWindow(timestamp);
        return await dialog.ShowDialog<bool>(owner);
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.UtcNow; }
    }

    private static DateTime? TryParseSnapshotTimestamp(string path)
    {
        const string prefix = "autosave-";
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var stamp = name[prefix.Length..];
        return DateTime.TryParseExact(
            stamp,
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
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

}
