using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Remex.Desktop.Models;

namespace Remex.Desktop.Converters;

/// <summary>
/// Multi-value converter: highlights the part of a command palette row's text that matched the
/// current search query, so the user can see WHY a row is showing rather than just THAT it is.
/// </summary>
/// <remarks>
/// <see cref="CommandPaletteEntry.Matches(string)"/> (RemEx-x6a70) already filters rows by a
/// case-insensitive substring against <c>Label</c>, <c>Category</c> and <c>SearchAliases</c>, but the
/// row itself gave no visual sign of which characters caused the match. Bound as a
/// <c>MultiBinding</c> over a row's <c>Label</c> (or <c>Category</c>) and the live
/// <c>CommandPaletteViewModel.SearchText</c>, this rebuilds the <see cref="TextBlock.Inlines"/>
/// collection into plain and bold <see cref="Run"/>s.
///
/// Bold is the ONLY styling this converter applies. It must never set <c>Foreground</c>,
/// <c>Opacity</c> or <c>FontSize</c> on a <see cref="Run"/> — the row's selected/hover colour rules
/// (RemEx-o9gd, in <c>CommandPaletteWindow.axaml</c>'s <c>ListBox.Styles</c>) are declared on the
/// containing <c>TextBlock</c> and must keep applying to every run inside it, matched or not.
/// </remarks>
public sealed class MatchHighlightConverter : IMultiValueConverter
{
    public static readonly MatchHighlightConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Count > 0 && values[0] is string label ? label : string.Empty;
        var query = values.Count > 1 && values[1] is string searchText ? searchText : null;

        var inlines = new InlineCollection();
        var segments = Segment(text, query);

        if (segments.Count == 0)
        {
            // Unusable input (wrong types, or genuinely empty text) still needs exactly one run so
            // the TextBlock renders something rather than nothing.
            inlines.Add(new Run(string.Empty));
            return inlines;
        }

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
                run.FontWeight = FontWeight.Bold;

            inlines.Add(run);
        }

        return inlines;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into match / non-match segments against every non-overlapping,
    /// case-insensitive occurrence of <paramref name="query"/>. An empty query, a null or empty
    /// <paramref name="text"/>, or a query that never occurs all degrade to something render-safe
    /// rather than throwing: an empty list for null/empty text, otherwise one non-match segment
    /// covering the whole text.
    /// </summary>
    public static IReadOnlyList<(string Text, bool IsMatch)> Segment(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<(string, bool)>();

        var trimmedQuery = query?.Trim();
        if (string.IsNullOrEmpty(trimmedQuery))
            return new[] { (text, false) };

        var segments = new List<(string Text, bool IsMatch)>();
        var index = 0;
        while (index < text.Length)
        {
            var matchIndex = text.IndexOf(trimmedQuery, index, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                segments.Add((text[index..], false));
                break;
            }

            if (matchIndex > index)
                segments.Add((text[index..matchIndex], false));

            segments.Add((text.Substring(matchIndex, trimmedQuery.Length), true));
            index = matchIndex + trimmedQuery.Length;
        }

        return segments;
    }
}
