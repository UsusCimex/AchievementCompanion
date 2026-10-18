using AC.Core;

namespace AC.Infrastructure;

/// <summary>Что делать с Steam при ручной отметке (раздел 6 ТЗ).</summary>
public enum WriteMode
{
    /// <summary>Только отметка в приложении, Steam не трогаем.</summary>
    MarkOnly = 0,
    /// <summary>Отметка и сразу запись в Steam через помощник.</summary>
    MarkAndUnlock = 1,
    /// <summary>Отметки копятся, в Steam отправляются по кнопке.</summary>
    Queue = 2,
}

/// <summary>Откуда брать статус "открыто/нет" при автообновлении.</summary>
public enum StatusSource
{
    /// <summary>Steam Web API: нужен ключ и открытые "Сведения об играх".</summary>
    WebApi = 0,
    /// <summary>Через Steam-клиент (помощник): работает при скрытом профиле, на секунду показывает "в игре".</summary>
    SteamClient = 1,
}

public sealed class AppSettings
{
    /// <summary>API-ключ, зашифрованный DPAPI (base64). Открытым текстом не хранится.</summary>
    public string ApiKeyProtected { get; set; } = "";
    public ulong SteamId64 { get; set; }
    /// <summary>Язык схемы в терминах Steam: russian, english...</summary>
    public string Language { get; set; } = "russian";
    public Percentile Percentile { get; set; } = Percentile.P50;
    public WriteMode WriteMode { get; set; } = WriteMode.MarkOnly;
    /// <summary>Пользователь подтвердил, что понимает, что делает запись в Steam.</summary>
    public bool WriteModeConfirmed { get; set; }
    public bool HideSpoilers { get; set; } = true;
    public char CsvSeparator { get; set; } = ';';

    public string OverlayToggleHotkey { get; set; } = "Ctrl+Shift+A";
    public string OverlayInteractHotkey { get; set; } = "Ctrl+Shift+S";
    public double OverlayLeft { get; set; } = 40;
    public double OverlayTop { get; set; } = 120;
    public double OverlayOpacity { get; set; } = 0.85;
    public int OverlayNearestCount { get; set; } = 5;

    /// <summary>Как часто опрашивать Web API во время игры, секунды (если файл статистики не меняется).</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Источник статуса для автообновления (по изменению файла статистики и по таймеру).</summary>
    public StatusSource StatusSource { get; set; } = StatusSource.WebApi;

    public uint LastAppId { get; set; }
}
