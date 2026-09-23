using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UltimateLocalAI.Converters;

public sealed class RoleToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), "user", StringComparison.OrdinalIgnoreCase) ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RoleToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            string.Equals(value?.ToString(), "user", StringComparison.OrdinalIgnoreCase) ? "#007AFF" : "#FFFFFF"));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RoleToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            string.Equals(value?.ToString(), "user", StringComparison.OrdinalIgnoreCase) ? "#FFFFFF" : "#1D1D1F"));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RoleToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), "user", StringComparison.OrdinalIgnoreCase) ? "Вы" : "Локальный ИИ";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
