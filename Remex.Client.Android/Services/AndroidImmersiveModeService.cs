using Android.App;
using Android.Views;
using Remex.Client.Services;
using System.Runtime.Versioning;
using Android.OS;

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
        // Only use InsetsController APIs on Android 11 (API 30) and above.
        if (Build.VERSION.SdkInt < BuildVersionCodes.R)
        {
            // Pre-Android 11: immersive mode not handled here.
            return;
        }

        var controller = _activity.Window?.InsetsController;
        if (controller is not null)
        {
            controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
            controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
    }

    public void ExitImmersiveMode()
    {
        // Only use InsetsController APIs on Android 11 (API 30) and above.
        if (Build.VERSION.SdkInt < BuildVersionCodes.R)
        {
            // Pre-Android 11: immersive mode not handled here.
            return;
        }

        var controller = _activity.Window?.InsetsController;
        if (controller is not null)
        {
            controller.Show(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
        }
    }
}
