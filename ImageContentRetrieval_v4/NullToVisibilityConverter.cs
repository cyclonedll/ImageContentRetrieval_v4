using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImageContentRetrieval_v4;

/// <summary>
/// 当绑定值为 null 时返回 <see cref="Visibility.Visible"/>，否则返回 <see cref="Visibility.Collapsed"/>。
/// 用于在图片尚未加载时显示占位提示文字。
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
