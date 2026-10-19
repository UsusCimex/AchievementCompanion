using System.Diagnostics;
using System.Globalization;
using System.Windows;
using AC.Infrastructure;

namespace AC.App;

public partial class SettingsWindow : Window
{
    private readonly GameService _svc;

    public SettingsWindow(GameService svc)
    {
        InitializeComponent();
        _svc = svc;
        var s = svc.Settings;
        ApiKeyBox.Password = svc.ApiKey;
        SteamIdBox.Text = s.SteamId64 == 0 ? "" : s.SteamId64.ToString(CultureInfo.InvariantCulture);
        LanguageBox.SelectedItem = Options.Languages.FirstOrDefault(l => l.Value == s.Language) ?? Options.Languages[0];
        WriteModeBox.SelectedItem = Options.WriteModes.FirstOrDefault(m => m.Value == s.WriteMode) ?? Options.WriteModes[0];
        StatusSourceBox.SelectedItem = Options.StatusSources.FirstOrDefault(m => m.Value == s.StatusSource) ?? Options.StatusSources[0];
        ThemeBox.SelectedItem = Options.Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Options.Themes[0];
        HideSpoilersBox.IsChecked = s.HideSpoilers;
        ToggleHotkeyBox.Text = s.OverlayToggleHotkey;
        InteractHotkeyBox.Text = s.OverlayInteractHotkey;
        OpacitySlider.Value = s.OverlayOpacity;
        NearestBox.Text = s.OverlayNearestCount.ToString(CultureInfo.InvariantCulture);
        PollBox.Text = s.PollSeconds.ToString(CultureInfo.InvariantCulture);
        DataDirBox.Text = AppPaths.DataRoot;
        DataDirHint.Text = AppPaths.IsPortable
            ? "Портативный режим: данные лежат в папке data рядом с программой."
            : $"Игры, настройки, кеш картинок и журнал ошибок (app.log). Чтобы держать данные рядом с программой, создай рядом с ней папку data; другую папку можно задать переменной окружения {AppPaths.DataDirVariable}.";
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.DataRoot) { UseShellExecute = true });

    private void OnGetKey(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://steamcommunity.com/dev/apikey") { UseShellExecute = true });

    private void OnDetectSteamId(object sender, RoutedEventArgs e)
    {
        var u = SteamLocal.MostRecentUser();
        if (u is null)
        {
            MessageBox.Show(this, "Не нашёл loginusers.vdf - Steam не установлен или ни разу не входили.", Title);
            return;
        }
        SteamIdBox.Text = u.Value.SteamId.ToString(CultureInfo.InvariantCulture);
        MessageBox.Show(this, $"Найден аккаунт: {u.Value.PersonaName}", Title);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = _svc.Settings;

        var idText = SteamIdBox.Text.Trim();
        ulong steamId = 0;
        if (idText.Length > 0 && (!ulong.TryParse(idText, out steamId) || steamId < SteamLocal.SteamId64Base))
        {
            MessageBox.Show(this, "SteamID64 - 17-значное число, начинается с 7656119...", Title);
            return;
        }

        foreach (var hk in new[] { ToggleHotkeyBox.Text, InteractHotkeyBox.Text })
        {
            if (!HotkeyManager.TryParse(hk, out _, out _))
            {
                MessageBox.Show(this, $"Не понял сочетание \"{hk}\". Пример: Ctrl+Shift+A", Title);
                return;
            }
        }

        var mode = (WriteModeBox.SelectedItem as Option<WriteMode>)?.Value ?? WriteMode.MarkOnly;
        if (mode != WriteMode.MarkOnly && !s.WriteModeConfirmed)
        {
            var answer = MessageBox.Show(this,
                "Приложение будет записывать отмеченные достижения в твой Steam-аккаунт через Steam-клиент (как SAM).\n\n" +
                "- Valve официально такое не поддерживает.\n" +
                "- Сайты-рейтинги (Steam Hunters, completionist.me) могут пометить профиль, если много достижений откроется в одну секунду.\n" +
                "- Записанное можно снять обратно (правый клик по строке).\n\nВключить?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            s.WriteModeConfirmed = true;
        }

        _svc.ApiKey = ApiKeyBox.Password;
        s.SteamId64 = steamId;
        s.Language = (LanguageBox.SelectedItem as Option<string>)?.Value ?? "russian";
        s.WriteMode = mode;
        s.StatusSource = (StatusSourceBox.SelectedItem as Option<StatusSource>)?.Value ?? StatusSource.WebApi;
        s.Theme = (ThemeBox.SelectedItem as Option<AppTheme>)?.Value ?? AppTheme.System;
        s.HideSpoilers = HideSpoilersBox.IsChecked == true;
        s.OverlayToggleHotkey = ToggleHotkeyBox.Text.Trim();
        s.OverlayInteractHotkey = InteractHotkeyBox.Text.Trim();
        s.OverlayOpacity = OpacitySlider.Value;
        if (int.TryParse(NearestBox.Text.Trim(), out var n) && n is > 0 and <= 20) s.OverlayNearestCount = n;
        if (int.TryParse(PollBox.Text.Trim(), out var poll) && poll >= 15) s.PollSeconds = poll;
        _svc.SaveSettings();
        DialogResult = true;
    }
}
