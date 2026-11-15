using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RadioDeck.Converters;

/// <summary>
/// Converts (value, min, max) to a star GridLength for a two-column "fill/rest" ProgressBar template.
/// ConverterParameter: "fill" or "rest".
/// </summary>
public sealed class VuBarGridLengthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var value = values.Length > 0 ? System.Convert.ToDouble(values[0], CultureInfo.InvariantCulture) : 0.0;
            var min = values.Length > 1 ? System.Convert.ToDouble(values[1], CultureInfo.InvariantCulture) : 0.0;
            var max = values.Length > 2 ? System.Convert.ToDouble(values[2], CultureInfo.InvariantCulture) : 100.0;

            if (max <= min)
                return new GridLength(0, GridUnitType.Star);

            var t = (value - min) / (max - min);
            if (double.IsNaN(t) || double.IsInfinity(t))
                t = 0;

            t = Math.Clamp(t, 0.0, 1.0);

            // Keep a stable star basis to avoid layout "jitter" on tiny changes.
            const double basis = 1000.0;
            var mode = (parameter as string) ?? string.Empty;

            var stars = mode.Equals("rest", StringComparison.OrdinalIgnoreCase)
                ? (1.0 - t) * basis
                : t * basis;

            return new GridLength(stars, GridUnitType.Star);
        }
        catch
        {
            return new GridLength(0, GridUnitType.Star);
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

