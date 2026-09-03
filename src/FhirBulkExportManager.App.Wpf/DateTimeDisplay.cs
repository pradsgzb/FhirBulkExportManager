using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FhirBulkExportManager.Application.Configuration;

namespace FhirBulkExportManager.App.Wpf;

public static class DateTimeDisplayPolicy
{
    public static DateDisplayMode Mode { get; set; } = DateDisplayMode.Utc;

    public static string Format(DateTimeOffset? value, CultureInfo? culture = null)
    {
        if (value is null)
        {
            return "—";
        }

        culture ??= CultureInfo.CurrentCulture;
        return Mode == DateDisplayMode.Local
            ? value.Value.ToLocalTime().ToString("G", culture)
            : value.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", culture);
    }
}

public sealed class DateTimeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset timestamp => DateTimeDisplayPolicy.Format(timestamp, culture),
        _ => "—",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

public sealed class FileCountProgressConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values.Any(value => ReferenceEquals(value, DependencyProperty.UnsetValue)))
        {
            return "0 / 0";
        }

        var downloaded = System.Convert.ToInt32(values[0], CultureInfo.InvariantCulture);
        var total = System.Convert.ToInt32(values[1], CultureInfo.InvariantCulture) +
                    System.Convert.ToInt32(values[2], CultureInfo.InvariantCulture) +
                    System.Convert.ToInt32(values[3], CultureInfo.InvariantCulture);
        return $"{downloaded:N0} / {total:N0}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => System.Windows.Data.Binding.DoNothing).ToArray();
}
