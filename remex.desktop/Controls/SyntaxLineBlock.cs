using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Remex.Desktop.Services.FilePreview;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Controls;

/// <summary>
/// One line of the File Transfer text preview, coloured by <see cref="SyntaxTokenizer"/>'s spans (2026-10-08 redesign).
/// A plain <see cref="TextBlock"/> cannot bind its inlines, so this builds them from <see cref="Line"/>.
/// </summary>
/// <remarks>
/// COLOURS COME FROM THE PALETTE'S ROLE KEYS, never hex literals, so every preset, seed and contrast level colours the
/// preview the same way it colours the rest of the screen. They are looked up again when the theme changes.
/// </remarks>
public sealed class SyntaxLineBlock : TextBlock
{
    public static readonly StyledProperty<PreviewLine?> LineProperty =
        AvaloniaProperty.Register<SyntaxLineBlock, PreviewLine?>(nameof(Line));

    public PreviewLine? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty)
            Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();
        if (Line is not { } line)
            return;

        var at = 0;
        foreach (var span in line.Spans)
        {
            if (span.Start > at)
                inlines.Add(new Run(line.Text[at..span.Start]));
            var run = new Run(line.Text.Substring(span.Start, span.Length));
            if (BrushFor(span.Kind) is { } brush)
                run.Foreground = brush;
            if (span.Kind is SyntaxKind.LogError)
                run.FontWeight = FontWeight.SemiBold;
            inlines.Add(run);
            at = span.Start + span.Length;
        }
        if (at < line.Text.Length)
            inlines.Add(new Run(line.Text[at..]));
    }

    private IBrush? BrushFor(SyntaxKind kind)
    {
        var key = kind switch
        {
            SyntaxKind.Comment or SyntaxKind.LogDebug or SyntaxKind.Timestamp => "TextMutedBrush",
            SyntaxKind.String => "SystemSuccessBrush",
            SyntaxKind.Number or SyntaxKind.Keyword => "PaletteTertiaryBrush",
            SyntaxKind.Key => "AccentPrimaryBrush",
            SyntaxKind.LogError => "SystemErrorBrush",
            SyntaxKind.LogWarning => "SystemWarningBrush",
            _ => null,
        };
        return key is not null && this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
    }
}
