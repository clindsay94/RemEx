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
    /// The active culture's name, e.g. <c>en</c> or <c>pt-BR</c>.
    /// </summary>
    /// <remarks>
    /// Exists so a binding can subscribe to culture changes without pretending to want a string.
    /// <see cref="Converters.PrefixedLabelConverter"/> needs that: it labels ComboBox items whose
    /// source is a plain <see cref="string"/>, which raises no notification of its own, so without
    /// a notifying binding alongside it the labels render once and never refresh — leaving those
    /// pickers in the previous language after a live switch. Binding a real resource key would work
    /// today but breaks silently if that key is ever renamed; this property cannot be, and reads as
    /// deliberate at the call site. The <see cref="string.Empty"/> notification below already covers
    /// it, so nothing extra has to be raised.
    /// </remarks>
    public string CultureTag => _culture.Name;

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
