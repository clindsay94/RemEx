using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Remex.Desktop.Converters;

/// <summary>
/// Markup extension for concise localization bindings in XAML.
/// Usage: Text="{local:Localize Nav_Home}"
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocalizeExtension() { }
    public LocalizeExtension(string key) { Key = key; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = Services.LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
    }
}
