using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class ShellView : UserControl
{
    private TransitioningContentControl? _pageHost;
    private Border? _settingsPanel;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _pageHost = this.FindControl<TransitioningContentControl>("PageHost");
        _settingsPanel = this.FindControl<Border>("SettingsPanel");

        if (DataContext is ShellViewModel vm)
            vm.BeginWelcomeSplash();

        var bootSplash = this.FindControl<Controls.Splash.SkiaSplashControl>("BootSplash");
        if (bootSplash != null && !_bootSplashHooked)
        {
            // Guarded because OnLoaded runs again on every reattach to the visual tree. Unguarded,
            // each reattach added another SequenceCompleted handler and OnBootSequenceCompleted then
            // ran once per attach - the same subscribe-without-unsubscribe shape RemEx-wcte fixed
            // elsewhere in this file's neighbours.
            _bootSplashHooked = true;

            bootSplash.SequenceCompleted += () =>
            {
                if (DataContext is ShellViewModel vm2)
                    vm2.OnBootSequenceCompleted();
            };

            // SkiaSplashControl.Dispose detaches the DispatcherTimer's Tick handler, without which the
            // timer keeps a strong reference back into the control forever. It must be called on FINAL
            // teardown only: OnDetachedFromVisualTree deliberately calls Stop() and not Dispose(),
            // because the control can be reattached and OnAttachedToVisualTree resumes ticking through
            // that same subscription. Disposing on unload would leave a reattached splash frozen, so the
            // owning window's Closed event is the correct hook - it fires once, and only when the
            // control genuinely will not come back. (RemEx-wcte: the Dispose existed but nothing called
            // it, which made the fix dead code.)
            if (TopLevel.GetTopLevel(this) is Window owner)
                owner.Closed += (_, _) => bootSplash.Dispose();
        }
    }

    private bool _bootSplashHooked;

    private ShellViewModel? _previousVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_previousVm != null)
        {
            _previousVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (DataContext is ShellViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            _previousVm = vm;
        }
        else
        {
            _previousVm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSettingsPanelOpen) && _settingsPanel != null && sender is ShellViewModel settingsVm)
        {
            if (settingsVm.IsSettingsPanelOpen)
                _settingsPanel.Classes.Add("open");
            else
                _settingsPanel.Classes.Remove("open");
        }

        if (e.PropertyName == nameof(ShellViewModel.TransitionType) && _pageHost != null && sender is ShellViewModel vm)
        {

            // The former IsAndroid early-return was unreachable here (RemEx-f167).
            _pageHost.PageTransition = vm.TransitionType switch
            {
                0 => new PageSlide(TimeSpan.FromMilliseconds(250), vm.TransitionDirection >= 0 ? PageSlide.SlideAxis.Horizontal : PageSlide.SlideAxis.Horizontal),
                1 => new PageSlide(TimeSpan.FromMilliseconds(250), PageSlide.SlideAxis.Vertical),
                2 => new CrossFade(TimeSpan.FromMilliseconds(300)),
                3 => new CompositePageTransition
                {
                    PageTransitions =
                    {
                        new CrossFade(TimeSpan.FromMilliseconds(200)),
                        new PageSlide(TimeSpan.FromMilliseconds(300), PageSlide.SlideAxis.Horizontal),
                    }
                },
                _ => new CrossFade(TimeSpan.FromMilliseconds(300)),
            };
        }
    }

    private void OnSettingsBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm)
            vm.IsSettingsPanelOpen = false;
    }
}
