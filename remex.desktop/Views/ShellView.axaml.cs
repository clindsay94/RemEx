using System;
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
        if (bootSplash != null)
            bootSplash.SequenceCompleted += () =>
            {
                if (DataContext is ShellViewModel vm2)
                    vm2.OnBootSequenceCompleted();
            };
    }

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
