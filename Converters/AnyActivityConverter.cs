using System;
using System.Globalization;
using System.Windows.Data;

namespace RadioDeck.Converters;

/// <summary>
/// Returns true if any input indicates activity:
/// - any bool == true
/// - any numeric value > threshold (default: 1)
/// Use for "RX/TX active" visuals where some sources may not expose a dedicated detected flag.
/// </summary>
public sealed class AnyActivityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var threshold = 1.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            threshold = t;

        if (values is null)
            return false;

        foreach (var v in values)
        {
            if (v is bool b && b)
                return true;

            try
            {
                if (v is null)
                    continue;

                var d = System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (!double.IsNaN(d) && !double.IsInfinity(d) && d > threshold)
                    return true;
            }
            catch
            {
                // ignore non-numeric
            }
        }

        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

