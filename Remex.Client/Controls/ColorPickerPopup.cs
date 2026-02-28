using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Remex.Client.Controls;

/// <summary>
/// A lightweight HSV color picker shown in a popup/flyout.
/// Contains a hue slider, saturation/value pad, hex input, and preview swatch.
/// </summary>
public class ColorPickerPopup : ContentControl
{
    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<ColorPickerPopup, Color>(nameof(SelectedColor), Colors.White);

    public static readonly StyledProperty<string> ElementLabelProperty =
        AvaloniaProperty.Register<ColorPickerPopup, string>(nameof(ElementLabel), "Color");

    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public string ElementLabel
    {
        get => GetValue(ElementLabelProperty);
        set => SetValue(ElementLabelProperty, value);
    }

    /// <summary>Raised when the user confirms a color selection.</summary>
    public event EventHandler<Color>? ColorConfirmed;

    private double _hue;
    private double _saturation = 1.0;
    private double _valueBrightness = 1.0;
    private Canvas? _svPad;
    private Border? _previewSwatch;
    private TextBox? _hexInput;
    private bool _updatingHex;

    public ColorPickerPopup()
    {
        BuildUI();
        UpdateFromColor(SelectedColor);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedColorProperty && !_updatingHex)
        {
            UpdateFromColor(SelectedColor);
        }
    }

    private void BuildUI()
    {
        var root = new StackPanel
        {
            Spacing = 8,
            Width = 220,
            Margin = new Thickness(8)
        };

        // Label
        root.Children.Add(new TextBlock
        {
            Text = ElementLabel,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#8888AA")),
        });

        // SV Pad (Saturation-Value 2D area)
        _svPad = new Canvas
        {
            Width = 200,
            Height = 120,
            ClipToBounds = true,
            Background = Brushes.Transparent,
        };
        var svBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _svPad,
        };
        _svPad.PointerPressed += SvPad_PointerPressed;
        _svPad.PointerMoved += SvPad_PointerMoved;
        root.Children.Add(svBorder);

        // Hue slider
        var hueSlider = new Slider
        {
            Minimum = 0,
            Maximum = 360,
            Value = _hue,
            Width = 200,
            Height = 20,
        };
        hueSlider.PropertyChanged += (s, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                _hue = hueSlider.Value;
                UpdateColor();
                UpdateSvPadBackground();
            }
        };
        root.Children.Add(hueSlider);

        // Hex input + preview row
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        _hexInput = new TextBox
        {
            Width = 100,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, monospace"),
            Background = new SolidColorBrush(Color.Parse("#12121E")),
            Foreground = new SolidColorBrush(Color.Parse("#C0C0FF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2A3E")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4),
            Watermark = "#RRGGBB",
        };
        _hexInput.KeyDown += HexInput_KeyDown;
        hexRow.Children.Add(_hexInput);

        _previewSwatch = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(SelectedColor),
        };
        hexRow.Children.Add(_previewSwatch);

        var applyBtn = new Button
        {
            Content = "✓",
            FontSize = 14,
            Width = 32,
            Height = 32,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#4A3AFF")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
        };
        applyBtn.Click += (_, _) => ColorConfirmed?.Invoke(this, SelectedColor);
        hexRow.Children.Add(applyBtn);

        root.Children.Add(hexRow);

        Content = root;
    }

    private void SvPad_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateSvFromPointer(e.GetPosition(_svPad));
    }

    private void SvPad_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(_svPad).Properties.IsLeftButtonPressed)
        {
            UpdateSvFromPointer(e.GetPosition(_svPad));
        }
    }

    private void UpdateSvFromPointer(Point pos)
    {
        if (_svPad == null) return;
        _saturation = Math.Clamp(pos.X / _svPad.Width, 0, 1);
        _valueBrightness = Math.Clamp(1.0 - (pos.Y / _svPad.Height), 0, 1);
        UpdateColor();
    }

    private void HexInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _hexInput?.Text is { } hex)
        {
            try
            {
                var cleanHex = hex.StartsWith('#') ? hex : "#" + hex;
                var color = Color.Parse(cleanHex);
                _updatingHex = true;
                SelectedColor = color;
                UpdateFromColor(color);
                _updatingHex = false;
            }
            catch
            {
                // Invalid hex, ignore
            }
        }
    }

    private void UpdateColor()
    {
        var color = HsvToRgb(_hue, _saturation, _valueBrightness);
        _updatingHex = true;
        SelectedColor = color;
        if (_previewSwatch != null)
            _previewSwatch.Background = new SolidColorBrush(color);
        if (_hexInput != null)
            _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _updatingHex = false;
    }

    private void UpdateFromColor(Color color)
    {
        RgbToHsv(color, out _hue, out _saturation, out _valueBrightness);
        if (_previewSwatch != null)
            _previewSwatch.Background = new SolidColorBrush(color);
        if (_hexInput != null)
            _hexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        UpdateSvPadBackground();
    }

    private void UpdateSvPadBackground()
    {
        if (_svPad == null) return;
        // Render a hue-tinted background for the SV pad
        var hueColor = HsvToRgb(_hue, 1.0, 1.0);
        _svPad.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(hueColor, 1),
            }
        };
    }

    // ═══════════════ Color Math ═══════════════

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return new Color(255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max > 0 ? delta / max : 0;

        if (delta == 0)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            h = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            h = 60 * (((r - g) / delta) + 4);
        }

        if (h < 0) h += 360;
    }
}
