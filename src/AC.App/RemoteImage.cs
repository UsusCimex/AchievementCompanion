using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// Картинка из сети фоном Border: local:RemoteImage.DecodeWidth="96" local:RemoteImage.Url="{Binding Icon}".
/// Своя загрузка вместо BitmapImage с http-адресом: у того встроенный кеш WPF ломается, когда один адрес
/// грузится одновременно с разной шириной (шапка и библиотека, список и оверлей) - картинка остаётся пустой,
/// а на .NET Core он к тому же качает всё заново при каждом запуске.
/// </summary>
public static class RemoteImage
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(RemoteImage), new PropertyMetadata(null, OnChanged));

    /// <summary>Ширина декодирования в пикселях (с запасом на высокий DPI). 0 - как в файле.</summary>
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(RemoteImage), new PropertyMetadata(0, OnChanged));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int value) => d.SetValue(DecodeWidthProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        var url = GetUrl(border);
        int width = GetDecodeWidth(border);
        if (string.IsNullOrEmpty(url))
        {
            border.Background = null;
            return;
        }
        if (ImageCache.TryGet(url, width) is { } ready)
        {
            border.Background = Brush(ready);
            return;
        }
        border.Background = null;
        _ = LoadAsync(border, url, width);
    }

    private static async Task LoadAsync(Border border, string url, int width)
    {
        var image = await ImageCache.GetAsync(url, width);
        // пока грузили, карточку могли переиспользовать под другое достижение
        if (image is null || GetUrl(border) != url || GetDecodeWidth(border) != width) return;
        border.Background = Brush(image);
        border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private static ImageBrush Brush(ImageSource image)
    {
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Картинки по адресу: сначала память (адрес + ширина), потом диск (cache\images в папке данных), потом сеть.
/// Вызывается из UI-потока; чтение, сеть и декодирование идут в фоне. Один запрос на адрес,
/// сколько бы карточек его ни ждали. Неудачный адрес (нет арта у игры) не дёргаем 10 минут.
/// </summary>
internal static class ImageCache
{
    private const int MemoryLimit = 800;
    private const long DiskLimitBytes = 300L * 1024 * 1024;
    private const long DiskTrimToBytes = 200L * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, BitmapSource> Memory = new();
    private static readonly Dictionary<string, Task<byte[]?>> Downloads = new();
    private static readonly Dictionary<string, DateTime> Failed = new();

    public static string Dir { get; } = Path.Combine(AppPaths.DataRoot, "cache", "images");

    public static BitmapSource? TryGet(string url, int width) => Memory.GetValueOrDefault(Key(url, width));

    public static async Task<BitmapSource?> GetAsync(string url, int width)
    {
        var key = Key(url, width);
        if (Memory.TryGetValue(key, out var hit)) return hit;
        if (Failed.TryGetValue(url, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromMinutes(10)) return null;

        if (!Downloads.TryGetValue(url, out var download))
        {
            download = LoadBytesAsync(url);
            Downloads[url] = download;
        }
        byte[]? bytes;
        try { bytes = await download; }
        finally
        {
            if (Downloads.TryGetValue(url, out var current) && current == download) Downloads.Remove(url);
        }

        var image = bytes is null ? null : await Task.Run(() => Decode(bytes, width, url));
        if (image is null)
        {
            Failed[url] = DateTime.UtcNow;
            // битый файл в кеше (например, оборванная запись) - удаляем, в следующий раз скачаем заново
            try { File.Delete(PathOf(url)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
        if (Memory.Count >= MemoryLimit) Memory.Clear();
        Memory[key] = image;
        return image;
    }

    /// <summary>Если кеш на диске разросся, удаляем самые старые файлы. В фоне, при запуске.</summary>
    public static void TrimInBackground() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new DirectoryInfo(Dir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            if (total <= DiskLimitBytes) return;
            foreach (var f in files)
            {
                if (total <= DiskTrimToBytes) break;
                total -= f.Length;
                f.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Очистка кеша картинок", e);
        }
    });

    private static async Task<byte[]?> LoadBytesAsync(string url)
    {
        var path = PathOf(url);
        try
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* покажем и без кеша на диске */ }
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    private static BitmapSource? Decode(byte[] bytes, int width, string url)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            if (width > 0) image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnLoad;
            // IgnoreImageCache здесь нельзя: кеш WPF привязан к адресу, а у потока его нет - падает ArgumentNullException.
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e)
        {
            // битая или неизвестного формата картинка не должна ронять интерфейс - просто без неё
            AppLog.Error("Картинка " + url, e);
            return null;
        }
    }

    private static string Key(string url, int width) => url + "|" + width;

    private static string PathOf(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        var ext = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath) : "";
        return Path.Combine(Dir, hash + (ext.Length is > 1 and <= 5 ? ext : ".img"));
    }
}
