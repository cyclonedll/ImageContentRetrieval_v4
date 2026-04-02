using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ImageContentRetrieval_v4;

/// <summary>
/// 将图片文件路径转换为 60px 宽的缩略图 <see cref="BitmapImage"/>。
/// 使用 <see cref="BitmapImage.DecodePixelWidth"/> 以降低内存占用，
/// 并调用 <see cref="BitmapImage.Freeze"/> 使其可跨线程安全使用。
/// 内部使用缓存，避免 DataGrid 滚动时重复读取磁盘。
/// </summary>
public sealed class FilePathToThumbnailConverter : IValueConverter
{
    private readonly ConcurrentDictionary<string, BitmapImage?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
            return null;

        return _cache.GetOrAdd(path, LoadThumbnail);
    }

    private static BitmapImage? LoadThumbnail(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            using var ms = new MemoryStream(data);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 60;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
