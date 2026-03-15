using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Remex.Client.Converters;

public class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string base64 && !string.IsNullOrWhiteSpace(base64))
        {
            try
            {
                byte[] bytes = System.Convert.FromBase64String(base64);
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
