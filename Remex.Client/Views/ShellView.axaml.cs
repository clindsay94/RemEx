using System;
using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class ShellView : UserControl
{
    private TransitioningContentControl? _pageHost;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _pageHost = this.FindControl<TransitioningContentControl>("PageHost");

        if (DataContext is ShellViewModel vm)
            vm.BeginWelcomeSplash();

        var bootSplash = this.FindControl<Controls.BootSequenceControl>("BootSplash");
        if (bootSplash != null)
            bootSplash.SequenceCompleted += () =>
            {
                if (DataContext is ShellViewModel vm2)
                    vm2.OnBootSequenceCompleted();
            };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ShellViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.TransitionType) && _pageHost != null && sender is ShellViewModel vm)
        {

            if (OperatingSystem.IsAndroid())
            {
                _pageHost.PageTransition = new CrossFade(TimeSpan.FromMilliseconds(250));
                return;
            }

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

    private void OnSelectBaseDark(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.CustomizationVm?.SelectThemeCommand.Execute("BaseDarkGlass");
    }

    private void OnSelectCyberNOC(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.CustomizationVm?.SelectThemeCommand.Execute("CyberNOC");
    }

    private void OnSelectSolarFlare(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.CustomizationVm?.SelectThemeCommand.Execute("SolarFlare");
    }

    private void OnSelectMonolith(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.CustomizationVm?.SelectThemeCommand.Execute("Monolith");
    }
}
