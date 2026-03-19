using Android.App;
using Android.Views;
using Remex.Client.Services;
using System.Runtime.Versioning;

namespace Remex.Client.Android.Services;

[SupportedOSPlatform("android30.0")]
public class AndroidImmersiveModeService : IImmersiveModeService
{
    private readonly Activity _activity;

    public AndroidImmersiveModeService(Activity activity)
    {
        _activity = activity;
    }

    public void EnterImmersiveMode()
    {
        var controller = _activity.Window?.InsetsController;
        if (controller is not null)
        {
            controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
            controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
    }

    public void ExitImmersiveMode()
    {
        var controller = _activity.Window?.InsetsController;
        if (controller is not null)
        {
            controller.Show(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
        }
    }
}
