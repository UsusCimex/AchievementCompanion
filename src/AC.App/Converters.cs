using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AC.App;

/// <summary>true - Collapsed, false - Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : false;
}

/// <summary>Пустая строка - Collapsed, иначе Visible.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// URL картинки -> BitmapImage с уменьшенной декодировкой (ConverterParameter = ширина в пикселях).
/// Ошибки загрузки не роняют интерфейс: просто нет картинки. Уже загруженные кешируются.
/// </summary>
public sealed class UrlToImageConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> Cache = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || url.Length == 0) return null;
        int width = parameter is string p && int.TryParse(p, out var w) ? w : 0;
        var key = url + "|" + width;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (Cache.Count > 600) Cache.Clear();
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(url, UriKind.Absolute);
            if (width > 0) img.DecodePixelWidth = width;
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            // Не удалось скачать - убираем из кеша, чтобы в следующий раз попробовать снова.
            img.DownloadFailed += (_, _) => Cache.Remove(key);
            img.DecodeFailed += (_, _) => Cache.Remove(key);
            img.EndInit();
            Cache[key] = img;
            return img;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
