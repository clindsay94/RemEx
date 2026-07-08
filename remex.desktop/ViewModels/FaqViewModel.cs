using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// ViewModel for the standalone FAQ page. The FAQ list was previously a section inside
/// <see cref="AboutViewModel"/>; it now lives here as its own navigable destination.
/// The list is rebuilt whenever the app culture changes so the questions/answers stay localized.
/// </summary>
public partial class FaqViewModel : ObservableObject, IDisposable
{
    /// <summary>Localized question/answer pairs shown on the FAQ page.</summary>
    public ObservableCollection<FaqItem> FaqItems { get; } = new();

    public FaqViewModel()
    {
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        LoadFaq();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SetCulture raises "Item", "Item[]" and "" in sequence; rebuild once per switch (on the "" pulse).
        if (!string.IsNullOrEmpty(e.PropertyName)) return;

        FaqItems.Clear();
        LoadFaq();
    }

    private void LoadFaq()
    {
        for (int q = 1; q <= 11; q++)
        {
            FaqItems.Add(new FaqItem(
                LocalizationService.Instance[$"Faq_Q{q}_Question"],
                LocalizationService.Instance[$"Faq_Q{q}_Answer"]));
        }
    }

    public void Dispose() => LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
}
