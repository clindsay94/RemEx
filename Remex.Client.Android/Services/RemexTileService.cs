using Android.App;
using Android.Service.QuickSettings;
using Remex.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace Remex.Client.Android.Services;

[Service(Name = "com.remex.client.RemexTileService",
         Permission = global::Android.Manifest.Permission.BindQuickSettingsTile,
         Exported = true,
         Label = "Remex Lock")]
[IntentFilter(new[] { "android.service.quicksettings.action.QS_TILE" })]
public class RemexTileService : TileService
{
    public override void OnClick()
    {
        base.OnClick();
        if (Remex.Client.App.Services == null) return;
        var connVm = Remex.Client.App.Services.GetRequiredService<ConnectionViewModel>();
        if (connVm.IsConnected)
        {
            _ = connVm.SendCommandAsync("Lock");
        }
    }
}
