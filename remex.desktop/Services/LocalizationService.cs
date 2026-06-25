using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;

namespace Remex.Desktop.Services;

/// <summary>
/// Singleton localization service that provides bindable string properties.
/// When the culture changes, it raises PropertyChanged for all string keys,
/// causing XAML bindings to refresh live without app restart.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    private LocalizationService() { }

    /// <summary>
    /// Gets a localized string by key from the resource manager.
    /// </summary>
    public string this[string key] =>
        Localization.Strings.ResourceManager.GetString(key, _culture) ?? key;

    /// <summary>
    /// Changes the active culture and raises PropertyChanged for all bindings.
    /// </summary>
    public void SetCulture(string cultureCode)
    {
        CultureInfo newCulture;
        try
        {
            newCulture = new CultureInfo(cultureCode);
        }
        catch (CultureNotFoundException)
        {
            newCulture = new CultureInfo("en");
        }

        _culture = newCulture;
        Localization.Strings.Culture = newCulture;
        Thread.CurrentThread.CurrentUICulture = newCulture;
        CultureInfo.DefaultThreadCurrentUICulture = newCulture;

        // Notify ALL bindings to refresh (use all three standard names for max compatibility)
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
