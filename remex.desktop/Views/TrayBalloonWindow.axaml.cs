using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

/// <summary>
/// RemEx's tray balloon: a small undecorated window that appears in the tray corner, announces one
/// event and dismisses itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS IS DRAWN BY REMEX BECAUSE AVALONIA HAS NO BALLOON TO BORROW.</b> As of Avalonia 11.3.11
/// <see cref="TrayIcon"/> exposes exactly <c>Icon</c>, <c>IsVisible</c>, <c>Menu</c>,
/// <c>ToolTipText</c>, <c>Command</c> and <c>CommandParameter</c> — there is no balloon API on any
/// platform, so the question was never "which platforms support it" but "what do we show instead".
/// A native balloon would also have meant a second <c>Shell_NotifyIcon</c> registration on Windows
/// (RemEx's tray icon belongs to Avalonia and cannot be addressed from outside it) and a second,
/// differently-shaped path on Linux. Drawing it here is one code path for Windows and CachyOS
/// alike, and it is the only version of this surface that can follow the four PC themes.
/// </para>
/// <para>
/// It is positioned like <see cref="TrayFlyoutWindow"/> and shown without activation, so it never
/// steals focus from whatever the user is actually doing.
/// </para>
/// </remarks>
public partial class TrayBalloonWindow : Window
{
    /// <summary>How long a balloon stays up. A problem lingers, because it is the one the user
    /// cannot reconstruct from the app itself once it is gone.</summary>
    private static readonly TimeSpan OutcomeDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProblemDuration = TimeSpan.FromSeconds(15);

    private readonly DispatcherTimer _dismissTimer;

    public TrayBalloonWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer();
        _dismissTimer.Tick += OnDismissTick;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Shows (or re-uses) the balloon for one event. Call on the UI thread.
    /// </summary>
    /// <remarks>
    /// One window is re-used rather than one per event: two balloons would be drawn on top of each
    /// other in the same corner, and a stack of them in the corner the user is not looking at is
    /// noise rather than information. The newest event wins and the dismissal clock restarts.
    /// </remarks>
    public void Present(NotificationImportance importance, string title, string message)
    {
        var isProblem = importance == NotificationImportance.Problem;

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        GlyphText.Text = importance switch
        {
            NotificationImportance.Problem => "⚠",
            NotificationImportance.Outcome => "✔",
            _ => "ℹ",
        };

        if (isProblem)
            AccentStripe.Classes.Add("problem");
        else
            AccentStripe.Classes.Remove("problem");

        PositionAtTray();
        Show();

        _dismissTimer.Stop();
        _dismissTimer.Interval = isProblem ? ProblemDuration : OutcomeDuration;
        _dismissTimer.Start();
    }

    /// <summary>Parks the balloon just inside the bottom-right of the primary screen's work area.</summary>
    /// <remarks>
    /// <c>WorkingArea</c> is in physical pixels and <c>Width</c> is in logical
    /// units, so the window size is scaled INTO the screen's space rather than the result being
    /// scaled afterwards. Doing it the other way round lands correctly at 100% and drifts off the
    /// bottom-right corner on every scaled display.
    /// </remarks>
    private void PositionAtTray()
    {
        if (Screens.Primary is not { } screen)
            return;

        const double MarginLogical = 8;
        var scaling = screen.Scaling;
        var x = screen.WorkingArea.Right - (Width + MarginLogical) * scaling;
        var y = screen.WorkingArea.Bottom - (Height + MarginLogical) * scaling;
        Position = new PixelPoint((int)x, (int)y);
    }

    private void OnDismissTick(object? sender, EventArgs e)
    {
        _dismissTimer.Stop();
        Hide();
    }

    private void OnBalloonPressed(object? sender, PointerPressedEventArgs e)
    {
        _dismissTimer.Stop();
        Hide();

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.MainWindow ??= new MainWindow { DataContext = App.Services.GetRequiredService<ShellViewModel>() };
        desktop.MainWindow.Show();
        desktop.MainWindow.Activate();
        desktop.MainWindow.WindowState = WindowState.Normal;
    }

    private void OnCloseBalloon(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _dismissTimer.Stop();
        Hide();
    }
}

/// <summary>
/// The <see cref="ITrayBalloonSink"/> the notification service talks to. Owns the single
/// <see cref="TrayBalloonWindow"/> and creates it on first use.
/// </summary>
/// <remarks>
/// Lazy because the balloon is a window: constructing one during startup would run before the
/// windowing platform is ready, and most sessions never raise a notification while hidden at all.
/// The window is only ever hidden, never closed — but if something else closes it (application
/// shutdown does), the reference is dropped so the next balloon builds a fresh one instead of
/// throwing on a dead window.
/// </remarks>
public sealed class TrayBalloonSink : ITrayBalloonSink
{
    private TrayBalloonWindow? _window;

    /// <inheritdoc />
    public bool TryShow(NotificationImportance importance, string title, string message)
    {
        try
        {
            if (_window is null)
            {
                _window = new TrayBalloonWindow();
                _window.Closed += (_, _) => _window = null;
            }

            _window.Present(importance, title, message);
            return true;
        }
        catch (Exception ex)
        {
            // SAY SO RATHER THAN RETURN A QUIET false. The caller records the failure, but only this
            // frame knows WHY, and a balloon that cannot be drawn on one machine is exactly the kind
            // of report the diagnostics export exists to answer.
            InMemoryLogSink.Append(LogLevel.Warning, "Notification", "Tray balloon could not be shown", ex);
            _window = null;
            return false;
        }
    }
}
