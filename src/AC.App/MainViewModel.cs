using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AC.Core;
using AC.Infrastructure;
using Microsoft.Win32;

namespace AC.App;

public sealed record PlanItemViewModel(string Name, string Description, string Estimate, string Percent, string Kind);

public sealed record PlanGroupViewModel(string Label, string Longest, IReadOnlyList<PlanItemViewModel> Items);

public sealed record PlanSectionViewModel(string Title, IReadOnlyList<PlanGroupViewModel> Groups);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly GameService _svc;
    private readonly DispatcherTimer _runningTimer;
    private GameData? _game;
    private StatsFileWatcher? _watcher;
    private DateTime _lastPoll = DateTime.MinValue;
    private bool _refreshing;
    private uint _lastRunningAppId;
    private int _loadSeq;
    private int _busyCount;
    private string _completionFetchedText = "";

    public MainViewModel(GameService svc)
    {
        _svc = svc;
        LibraryView = CollectionViewSource.GetDefaultView(Library);
        LibraryView.Filter = o => o is OwnedGame g &&
            (string.IsNullOrWhiteSpace(_libraryFilter) || g.Name.Contains(_libraryFilter.Trim(), StringComparison.CurrentCultureIgnoreCase));

        _selectedSort = Options.Sorts[0];
        _kindFilter = Options.KindFilters[0];
        _selectedPercentile = Options.Percentiles.FirstOrDefault(p => p.Value == svc.Settings.Percentile) ?? Options.Percentiles[0];

        RefreshLibraryCommand = new AsyncCommand(RefreshLibraryAsync, OnError);
        LoadAppIdCommand = new AsyncCommand(LoadAppIdAsync, OnError);
        ReloadCommand = new AsyncCommand(() => ReloadAsync(force: true), OnError, () => _game != null);
        RefreshStateCommand = new AsyncCommand(RefreshStateAsync, OnError, () => _game != null);
        ReadViaBridgeCommand = new AsyncCommand(ReadViaBridgeAsync, OnError, () => _game != null);
        FlushQueueCommand = new AsyncCommand(FlushQueueAsync, OnError, () => QueueCount > 0);
        FetchCompletionCommand = new AsyncCommand(FetchCompletionAsync, OnError, () => _game != null);
        ApplyCompletionCommand = new RelayCommand(ApplyCompletion, () => _game != null);
        ApplyPlaytimeCommand = new RelayCommand(ApplyPlaytime, () => _game != null);
        CollectSampleCommand = new AsyncCommand(() => CollectSampleAsync(auto: false), OnError, () => _game != null && _svc.HasApiKey);
        OpenGuideCommand = new RelayCommand(p => OpenGuide(p as string), _ => _game != null && SelectedRow != null);
        ExportCsvCommand = new RelayCommand(p => Export("csv", p as string == "manual"), _ => _game != null);
        ExportJsonCommand = new RelayCommand(p => Export("json", p as string == "manual"), _ => _game != null);
        ExportTagsCommand = new RelayCommand(ExportTags, () => _game != null);
        ImportTagsCommand = new RelayCommand(ImportTags, () => _game != null);
        RevertCommand = new AsyncCommand(p => RevertAsync(p as AchievementRowViewModel), OnError, p => p is AchievementRowViewModel { IsUnlocked: true });
        UnlockInSteamCommand = new AsyncCommand(p => UnlockInSteamAsync(p, fromOverlay: false), OnError, p => _game != null && RowsFrom(p).Any(r => r.CanWriteToSteam));
        OverlayUnlockCommand = new AsyncCommand(p => UnlockInSteamAsync(p, fromOverlay: true), OnError, p => _game != null && RowsFrom(p).Any(r => r.CanWriteToSteam));
        OverlayMarkCommand = new RelayCommand(p => { if (p is AchievementRowViewModel r) MarkDoneFromOverlay(r); });

        _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _runningTimer.Tick += async (_, _) => await OnRunningTickAsync();
        _runningTimer.Start();

        foreach (var g in svc.CachedLibrary()) Library.Add(g);
    }

    public GameService Service => _svc;
    public GameData? Game => _game;
    public event Action<string>? Toast;

    // ---------- библиотека ----------

    public ObservableCollection<OwnedGame> Library { get; } = new();
    public ICollectionView LibraryView { get; }

    private string _libraryFilter = "";
    public string LibraryFilter
    {
        get => _libraryFilter;
        set { if (Set(ref _libraryFilter, value)) LibraryView.Refresh(); }
    }

    private OwnedGame? _selectedLibraryGame;
    public OwnedGame? SelectedLibraryGame
    {
        get => _selectedLibraryGame;
        set
        {
            if (!Set(ref _selectedLibraryGame, value) || value is null) return;
            _ = SafeAsync(() => LoadAsync(value.AppId, value.Name));
        }
    }

    private string _appIdInput = "";
    public string AppIdInput { get => _appIdInput; set => Set(ref _appIdInput, value); }

    // ---------- фильтры ----------

    private bool _hideDone;
    public bool HideDone { get => _hideDone; set { if (Set(ref _hideDone, value)) Recompute(rebuildRows: true); } }

    private bool _onlyManual;
    public bool OnlyManual { get => _onlyManual; set { if (Set(ref _onlyManual, value)) Recompute(rebuildRows: true); } }

    private bool _onlyHidden;
    public bool OnlyHidden { get => _onlyHidden; set { if (Set(ref _onlyHidden, value)) Recompute(rebuildRows: true); } }

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Recompute(rebuildRows: true); } }

    private Option<AchievementKind?> _kindFilter;
    public Option<AchievementKind?> KindFilter { get => _kindFilter; set { if (Set(ref _kindFilter, value)) Recompute(rebuildRows: true); } }

    private Option<SortMode> _selectedSort;
    public Option<SortMode> SelectedSort { get => _selectedSort; set { if (Set(ref _selectedSort, value)) Recompute(rebuildRows: true); } }

    private bool _descending;
    public bool Descending { get => _descending; set { if (Set(ref _descending, value)) Recompute(rebuildRows: true); } }

    private Option<Percentile> _selectedPercentile;
    public Option<Percentile> SelectedPercentile
    {
        get => _selectedPercentile;
        set
        {
            if (!Set(ref _selectedPercentile, value)) return;
            _svc.Settings.Percentile = value.Value;
            _svc.SaveSettings();
            Recompute(rebuildRows: true);
        }
    }

    public bool HideSpoilers => _svc.Settings.HideSpoilers;

    // ---------- данные текущей игры ----------

    public ObservableCollection<AchievementRowViewModel> Rows { get; } = new();
    public ObservableCollection<PlanSectionViewModel> PlanSections { get; } = new();
    /// <summary>Для оверлея: неоткрытые миссибл.</summary>
    public ObservableCollection<AchievementRowViewModel> OverlayMissable { get; } = new();
    /// <summary>Для оверлея: ближайшие по времени.</summary>
    public ObservableCollection<AchievementRowViewModel> OverlayNearest { get; } = new();

    private AchievementRowViewModel? _selectedRow;
    /// <summary>Выбранное достижение - для панели подробностей.</summary>
    public AchievementRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) OnPropertyChanged(nameof(HasSelectedRow)); }
    }
    public bool HasSelectedRow => _selectedRow != null;

    public bool HasGame => _game != null;
    public string? HeaderImageUrl => _game is null ? null : SteamImages.Header(_game.AppId);

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }

    private double _progressWithManualPercent;
    public double ProgressWithManualPercent { get => _progressWithManualPercent; private set => Set(ref _progressWithManualPercent, value); }

    private string _statUnlocked = "-";
    public string StatUnlocked { get => _statUnlocked; private set => Set(ref _statUnlocked, value); }

    private string _statUnlockedSub = "";
    public string StatUnlockedSub { get => _statUnlockedSub; private set => Set(ref _statUnlockedSub, value); }

    private string _statRemaining = "-";
    public string StatRemaining { get => _statRemaining; private set => Set(ref _statRemaining, value); }

    private string _statRemainingSub = "";
    public string StatRemainingSub { get => _statRemainingSub; private set => Set(ref _statRemainingSub, value); }

    private string _statPlayed = "-";
    public string StatPlayed { get => _statPlayed; private set => Set(ref _statPlayed, value); }

    private string _statPlayedSub = "";
    public string StatPlayedSub { get => _statPlayedSub; private set => Set(ref _statPlayedSub, value); }

    private string _gameName = "";
    public string GameName { get => _gameName; private set => Set(ref _gameName, value); }

    private string _gameTitle = "Игра не выбрана";
    public string GameTitle { get => _gameTitle; private set => Set(ref _gameTitle, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    private string _remainingText = "";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    private string _completionText = "";
    public string CompletionText { get => _completionText; private set => Set(ref _completionText, value); }

    private string _completionInput = "";
    /// <summary>Время до 100% в часах (ручной ввод).</summary>
    public string CompletionInput { get => _completionInput; set => Set(ref _completionInput, value); }

    private string _playtimeOverrideInput = "";
    /// <summary>Своё "реально наиграно", часы. Пусто - из Steam.</summary>
    public string PlaytimeOverrideInput { get => _playtimeOverrideInput; set => Set(ref _playtimeOverrideInput, value); }

    private string _playtimeOffsetInput = "";
    /// <summary>Сколько часов Steam не считать (до появления достижений, AFK).</summary>
    public string PlaytimeOffsetInput { get => _playtimeOffsetInput; set => Set(ref _playtimeOffsetInput, value); }

    private string _statusMessage = "Готово";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private int _queueCount;
    public int QueueCount
    {
        get => _queueCount;
        private set
        {
            if (!Set(ref _queueCount, value)) return;
            OnPropertyChanged(nameof(QueueText));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string QueueText => QueueCount > 0 ? $"Отправить в Steam ({QueueCount})" : "Очередь пуста";
    public bool QueueVisible => _svc.Settings.WriteMode == WriteMode.Queue;

    private bool _autoFollow = true;
    /// <summary>Сам переключаться на игру, которая запущена в Steam.</summary>
    public bool AutoFollow { get => _autoFollow; set => Set(ref _autoFollow, value); }

    public bool IsPending(string apiName) => _game?.PendingUnlocks.Contains(apiName) == true;

    // ---------- команды ----------

    public AsyncCommand RefreshLibraryCommand { get; }
    public AsyncCommand LoadAppIdCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand RefreshStateCommand { get; }
    public AsyncCommand ReadViaBridgeCommand { get; }
    public AsyncCommand FlushQueueCommand { get; }
    public AsyncCommand FetchCompletionCommand { get; }
    public RelayCommand ApplyCompletionCommand { get; }
    public RelayCommand ApplyPlaytimeCommand { get; }
    /// <summary>Собрать свою статистику по авторам обзоров (проценты и время).</summary>
    public AsyncCommand CollectSampleCommand { get; }
    /// <summary>Открыть в браузере, как получить выбранное достижение: steam, discussions, youtube, google.</summary>
    public RelayCommand OpenGuideCommand { get; }

    private string _sampleText = "";
    public string SampleText { get => _sampleText; private set => Set(ref _sampleText, value); }

    public string? HeroImageUrl => _game is null ? null : SteamImages.Hero(_game.AppId);
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ExportTagsCommand { get; }
    public RelayCommand ImportTagsCommand { get; }
    public AsyncCommand RevertCommand { get; }
    /// <summary>Записать в Steam сразу, как в SAM. Параметр - строка или список выбранных строк.</summary>
    public AsyncCommand UnlockInSteamCommand { get; }
    /// <summary>То же из оверлея: без диалогов (окно могло бы спрятаться за игрой).</summary>
    public AsyncCommand OverlayUnlockCommand { get; }
    public RelayCommand OverlayMarkCommand { get; }

    public async Task StartAsync()
    {
        if (!_svc.HasApiKey)
        {
            StatusMessage = "Задай API-ключ в настройках: без него работает только локальный кеш Steam";
        }
        else if (Library.Count == 0)
        {
            await SafeAsync(RefreshLibraryAsync);
        }

        var running = SteamLocal.RunningAppId();
        var start = running != 0 ? running : _svc.Settings.LastAppId;
        if (start != 0) await SafeAsync(() => LoadAsync(start, Library.FirstOrDefault(g => g.AppId == start)?.Name));
    }

    private async Task RefreshLibraryAsync()
    {
        await Busy("Загружаю библиотеку...", async () =>
        {
            var games = await _svc.RefreshLibraryAsync();
            Library.Clear();
            foreach (var g in games) Library.Add(g);
            StatusMessage = $"Игр в библиотеке: {games.Count}";
        });
    }

    private async Task LoadAppIdAsync()
    {
        if (!uint.TryParse(AppIdInput.Trim(), out var id) || id == 0)
        {
            StatusMessage = "AppID - это число из адреса страницы игры в Steam";
            return;
        }
        await LoadAsync(id, Library.FirstOrDefault(g => g.AppId == id)?.Name);
    }

    public async Task LoadAsync(uint appId, string? name)
    {
        var seq = ++_loadSeq;
        await Busy($"Загружаю {name ?? appId.ToString()}...", async () =>
        {
            var report = await _svc.LoadGameAsync(appId, name);
            if (seq != _loadSeq) return; // пока грузили, выбрали другую игру
            SetGame(report.Game);
            StatusMessage = report.Warnings.Count > 0
                ? string.Join(" | ", report.Warnings)
                : $"Загружено: {report.Game.Achievements.Count} достижений";
            AnnounceNew(report.NewlyUnlocked);
        });
        // При источнике "Steam-клиент" статус при загрузке не читался - читаем сейчас.
        if (seq == _loadSeq && _game?.AppId == appId &&
            (_svc.Settings.StatusSource == StatusSource.SteamClient || !_svc.HasApiKey) && _svc.Bridge.Exists)
            await SafeAsync(RefreshStateAsync);

        // Steam не отдал проценты (так бывает у новых игр) - один раз сами собираем статистику по игрокам.
        if (seq == _loadSeq && _game is { } g && g.AppId == appId && _svc.HasApiKey &&
            g.Achievements.Count > 0 && g.Achievements.All(a => a.GlobalPercent is null) && g.SampleFetchedAt is null)
            await SafeAsync(() => CollectSampleAsync(auto: true));
    }

    private async Task CollectSampleAsync(bool auto)
    {
        if (_game is null) return;
        var game = _game;
        var progress = new Progress<string>(m => StatusMessage = m);
        await Busy(auto ? "Steam не отдал проценты - собираю статистику по игрокам..." : "Собираю статистику по игрокам...", async () =>
        {
            var r = await _svc.CollectSampleAsync(game, progress);
            if (!ReferenceEquals(game, _game)) return;
            Recompute(rebuildRows: true);
            StatusMessage = $"Статистика собрана: {r.Players} игроков с открытым профилем" +
                            (r.CompletionMedianMinutes is { } c ? $", 100% у {r.Completers}, медиана {TimeEstimator.Format(c)}" : "");
        });
    }

    private void OpenGuide(string? kind)
    {
        if (_game is null || SelectedRow is null) return;
        var game = _game.Name.Length > 0 ? _game.Name : _game.AppId.ToString(CultureInfo.InvariantCulture);
        var ach = SelectedRow.Name;
        string q(string s) => Uri.EscapeDataString(s);
        var url = kind switch
        {
            "steam" => $"https://steamcommunity.com/app/{_game.AppId}/guides/?searchText={q(ach)}&browsefilter=trend&requiredtags%5B%5D=-1",
            "discussions" => $"https://steamcommunity.com/app/{_game.AppId}/discussions/search/?q={q(ach)}",
            "youtube" => $"https://www.youtube.com/results?search_query={q(game + " " + ach + " achievement")}",
            _ => $"https://www.google.com/search?q={q("\"" + game + "\" \"" + ach + "\" achievement guide")}",
        };
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = "Не удалось открыть браузер: " + e.Message;
        }
    }

    private async Task ReloadAsync(bool force)
    {
        if (_game is null) return;
        await Busy("Обновляю схему и статус...", async () =>
        {
            var seq = ++_loadSeq;
            var report = await _svc.LoadGameAsync(_game.AppId, _game.Name, forceSchema: force);
            if (seq != _loadSeq) return;
            SetGame(report.Game);
            StatusMessage = report.Warnings.Count > 0 ? string.Join(" | ", report.Warnings) : "Схема и статус обновлены";
            AnnounceNew(report.NewlyUnlocked);
        });
    }

    private async Task RefreshStateAsync()
    {
        if (_game is null || _refreshing) return;
        _refreshing = true;
        try
        {
            var game = _game;
            var fresh = await _svc.RefreshStateAutoAsync(game);
            _lastPoll = DateTime.Now;
            if (!ReferenceEquals(game, _game)) return;
            // Не пересобираем таблицу, пока пользователь что-то вводит в строке - иначе ввод пропадёт.
            bool editing = Keyboard.FocusedElement is FrameworkElement { DataContext: AchievementRowViewModel };
            Recompute(rebuildRows: fresh.Count > 0 && HideDone && !editing);
            AnnounceNew(fresh);
            if (fresh.Count == 0) StatusMessage = "Статус обновлён " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ReadViaBridgeAsync()
    {
        if (_game is null) return;
        await Busy("Читаю через Steam-клиент...", async () =>
        {
            var (fresh, error) = await _svc.RefreshViaBridgeAsync(_game);
            if (error != null)
            {
                StatusMessage = "Помощник: " + error;
                return;
            }
            Recompute(rebuildRows: true);
            AnnounceNew(fresh);
            StatusMessage = "Статус прочитан через Steam-клиент";
        });
    }

    private async Task FlushQueueAsync()
    {
        if (_game is null) return;
        if (MessageBox.Show($"Записать в Steam {QueueCount} достижений из очереди?", "Achievement Companion",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await Busy("Записываю в Steam...", async () =>
        {
            var r = await _svc.FlushQueueAsync(_game);
            Recompute(rebuildRows: true);
            StatusMessage = r.Error is null ? "Очередь отправлена в Steam" : "Не всё записалось: " + r.Error;
        });
    }

    private static IEnumerable<AchievementRowViewModel> RowsFrom(object? p) => p switch
    {
        AchievementRowViewModel r => new[] { r },
        System.Collections.IEnumerable list => list.OfType<AchievementRowViewModel>(),
        _ => Array.Empty<AchievementRowViewModel>(),
    };

    private async Task UnlockInSteamAsync(object? p, bool fromOverlay)
    {
        if (_game is null) return;
        var rows = RowsFrom(p).Where(r => r.CanWriteToSteam).ToList();
        if (rows.Count == 0) return;

        if (fromOverlay && !_svc.Settings.WriteModeConfirmed)
        {
            const string msg = "Сначала один раз запиши любое достижение из главного окна - там нужно подтвердить запись в Steam";
            StatusMessage = msg;
            Toast?.Invoke(msg);
            return;
        }

        if (fromOverlay) { /* без подтверждений: пользователь в игре */ }
        else if (!_svc.Settings.WriteModeConfirmed)
        {
            var answer = MessageBox.Show(
                "Достижение будет записано в твой Steam-аккаунт через Steam-клиент, как в SAM.\n\n" +
                "- Время открытия в Steam будет текущим.\n" +
                "- Valve официально такое не поддерживает; сайты-рейтинги могут пометить профиль, если много достижений откроется в одну секунду.\n" +
                "- Снять обратно: правый клик по строке, \"Снять это достижение в Steam\".\n\nПродолжить?",
                "Achievement Companion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            _svc.Settings.WriteModeConfirmed = true;
            _svc.SaveSettings();
        }
        else if (rows.Count > 1 &&
                 MessageBox.Show($"Записать в Steam {rows.Count} достижений?", "Achievement Companion",
                     MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var game = _game;
        await Busy("Записываю в Steam...", async () =>
        {
            var r = await _svc.UnlockInSteamAsync(game, rows.Select(x => x.ApiName).ToList());
            if (!ReferenceEquals(game, _game)) return;
            Recompute(rebuildRows: false);
            var names = string.Join(", ", rows.Select(x => x.Name));
            if (r.SteamWritten)
            {
                StatusMessage = "Записано в Steam: " + names + (r.Error is null ? "" : " | " + r.Error);
                Toast?.Invoke("В Steam: " + names);
            }
            else StatusMessage = "Не записано: " + (r.Error ?? "неизвестная ошибка");
        });
    }

    private async Task RevertAsync(AchievementRowViewModel? row)
    {
        if (_game is null || row is null) return;
        if (!_svc.WasWrittenByApp(_game.AppId, row.ApiName) &&
            MessageBox.Show("Это достижение получено не через приложение. Всё равно снять его в Steam?",
                "Achievement Companion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Busy("Снимаю в Steam...", async () =>
        {
            var r = await _svc.RevertInSteamAsync(_game, row.ApiName);
            Recompute(rebuildRows: false);
            StatusMessage = r.Error is null ? $"Снято в Steam: {row.Name}" : "Ошибка: " + r.Error;
        });
    }

    private async Task FetchCompletionAsync()
    {
        if (_game is null) return;
        await Busy("Спрашиваю completionist.me...", async () =>
        {
            var m = await _svc.FetchCompletionAsync(_game);
            if (m is null)
            {
                StatusMessage = "completionist.me не дал время (мало прошедших или сайт недоступен) - введи часы вручную";
                return;
            }
            CompletionInput = (m.Value / 60).ToString("0.#", CultureInfo.CurrentCulture);
            _completionFetchedText = CompletionInput;
            Recompute(rebuildRows: false);
            StatusMessage = "Время до 100% взято с completionist.me";
        });
    }

    private void ApplyCompletion()
    {
        if (_game is null) return;
        if (_game.CompletionMinutes is not null && _completionFetchedText.Length > 0 && CompletionInput == _completionFetchedText)
        {
            StatusMessage = "Время до 100% не менялось";
            return;
        }
        var s = CompletionInput.Trim().Replace(',', '.');
        double? minutes = null;
        if (s.Length > 0)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h <= 0)
            {
                StatusMessage = "Время до 100% - число часов, например 35 или 12,5";
                return;
            }
            minutes = h * 60;
        }
        _svc.SetCompletionMinutes(_game, minutes, "manual");
        Recompute(rebuildRows: false);
        StatusMessage = minutes is null ? "Время до 100% сброшено" : "Оценки времени пересчитаны";
    }

    private void ApplyPlaytime()
    {
        if (_game is null) return;
        if (!TryParseHours(PlaytimeOverrideInput, out var overrideMin) || !TryParseHours(PlaytimeOffsetInput, out var offsetMin))
        {
            StatusMessage = "Время - число часов, например 120 или 7,5. Пусто - не задано";
            return;
        }
        _svc.SetPlaytime(_game, overrideMin, offsetMin ?? 0);
        Recompute(rebuildRows: false);
        StatusMessage = overrideMin is not null
            ? "Наиграно задано вручную"
            : offsetMin is > 0 ? "Часть времени Steam не учитывается" : "Наиграно берётся из Steam";
    }

    /// <summary>Пусто - null (успех), число часов >= 0 - минуты, иначе ошибка.</summary>
    private static bool TryParseHours(string text, out double? minutes)
    {
        minutes = null;
        var s = text.Trim().Replace(',', '.');
        if (s.Length == 0) return true;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h < 0) return false;
        minutes = h * 60;
        return true;
    }

    private static string Hours(double? minutes) =>
        minutes is { } m ? (m / 60).ToString("0.#", CultureInfo.CurrentCulture) : "";

    // ---------- правки строк ----------

    internal async void OnManualDoneChanged(AchievementRowViewModel row, bool done)
    {
        if (_game is null) return;
        try
        {
            var report = await _svc.SetManualDoneAsync(_game, row.ApiName, done);
            if (report.Error != null) StatusMessage = report.Error;
            else if (report.SteamWritten) StatusMessage = $"Записано в Steam: {row.Name}";
            else StatusMessage = done ? $"Отмечено: {row.Name}" : $"Отметка снята: {row.Name}";
        }
        catch (Exception e)
        {
            OnError(e);
        }
        Recompute(rebuildRows: false);
    }

    internal void OnKindChanged(AchievementRowViewModel row, AchievementKind kind)
    {
        if (_game is null) return;
        _svc.SetKind(_game, row.ApiName, kind);
        Recompute(rebuildRows: false);
    }

    internal void OnGroupChanged(AchievementRowViewModel row, string group)
    {
        if (_game is null) return;
        _svc.SetGroup(_game, row.ApiName, group);
        Recompute(rebuildRows: false);
    }

    internal void OnNoteChanged(AchievementRowViewModel row, string note)
    {
        if (_game is null) return;
        _svc.SetNote(_game, row.ApiName, note);
    }

    internal void OnEstimateChanged(AchievementRowViewModel row, double? minutes)
    {
        if (_game is null) return;
        _svc.SetManualEstimate(_game, row.ApiName, minutes);
        Recompute(rebuildRows: false);
    }

    /// <summary>Отметка из оверлея.</summary>
    public void MarkDoneFromOverlay(AchievementRowViewModel row)
    {
        if (row.IsDone) return;
        row.ManualDone = true;
    }

    // ---------- экспорт / импорт ----------

    private void Export(string format, bool manualOnly)
    {
        if (_game is null) return;
        var scope = manualOnly ? ExportScope.ManualOnly : ExportScope.All;
        var dlg = new SaveFileDialog
        {
            FileName = $"{Sanitize(_game.Name)}_{_game.AppId}{(manualOnly ? "_not_counted" : "")}.{format}",
            Filter = format == "csv" ? "CSV (*.csv)|*.csv" : "JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (format == "csv") File.WriteAllBytes(dlg.FileName, Exporter.ToCsv(_game, scope, _svc.Settings.CsvSeparator));
            else File.WriteAllText(dlg.FileName, Exporter.ToJson(_game, scope));
            StatusMessage = "Сохранено: " + dlg.FileName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Не удалось сохранить (файл открыт в другой программе?): " + e.Message;
        }
    }

    private void ExportTags()
    {
        if (_game is null) return;
        var dlg = new SaveFileDialog { FileName = $"{Sanitize(_game.Name)}_{_game.AppId}_tags.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, TagPack.From(_game).ToJson());
            StatusMessage = "Разметка сохранена: " + dlg.FileName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Не удалось сохранить: " + e.Message;
        }
    }

    private void ImportTags()
    {
        if (_game is null) return;
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = _svc.ImportTags(_game, File.ReadAllText(dlg.FileName));
            Recompute(rebuildRows: true);
            StatusMessage = $"Разметка применена к {n} достижениям";
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Файл разметки не читается: " + e.Message;
        }
    }

    // ---------- пересчёт ----------

    private void SetGame(GameData game)
    {
        _game = game;
        GameTitle = $"{game.Name} ({game.AppId})";
        GameName = game.Name.Length > 0 ? game.Name : $"AppID {game.AppId}";
        SelectedRow = null;
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(HeaderImageUrl));
        OnPropertyChanged(nameof(HeroImageUrl));
        CompletionInput = Hours(game.CompletionMinutes);
        _completionFetchedText = game.CompletionSource == "manual" ? "" : CompletionInput;
        PlaytimeOverrideInput = Hours(game.PlaytimeOverrideMinutes);
        PlaytimeOffsetInput = game.PlaytimeOffsetMinutes > 0 ? Hours(game.PlaytimeOffsetMinutes) : "";
        RestartWatcher();
        Recompute(rebuildRows: true);
        OnPropertyChanged(nameof(Game));
        CommandManager.InvalidateRequerySuggested();
    }

    public void SettingsChanged()
    {
        OnPropertyChanged(nameof(HideSpoilers));
        OnPropertyChanged(nameof(QueueVisible));
        RestartWatcher();
        Recompute(rebuildRows: true);
    }

    public void Recompute(bool rebuildRows)
    {
        if (_game is null) return;
        var p = _selectedPercentile.Value;
        // Выведенное из своего прогресса время до 100% меняется с каждым открытием - пересчитываем.
        TimeEstimator.EstimateFromGlobalPercent(_game);

        if (rebuildRows)
        {
            var query = new AchievementQuery
            {
                HideDone = HideDone,
                OnlyManual = OnlyManual,
                OnlyHidden = OnlyHidden,
                Search = Search,
                Sort = _selectedSort.Value,
                Descending = Descending,
            };
            if (_kindFilter.Value is { } k) query.Kinds.Add(k);
            var selectedApi = SelectedRow?.ApiName;
            Rows.Clear();
            foreach (var r in query.Apply(_game, p)) Rows.Add(new AchievementRowViewModel(this, r));
            SelectedRow = selectedApi is null ? null : Rows.FirstOrDefault(r => r.ApiName == selectedApi);
        }

        var all = GameLogic.BuildRows(_game, p);
        if (!rebuildRows)
        {
            var fresh = all.ToDictionary(r => r.ApiName, StringComparer.Ordinal);
            foreach (var vm in Rows)
                if (fresh.TryGetValue(vm.ApiName, out var r)) vm.Update(r);
        }

        var plan = Planner.Build(_game, p);
        PlanSections.Clear();
        foreach (var s in plan.Sections)
        {
            var groups = s.Groups.Select(g => new PlanGroupViewModel(
                g.Label.Length > 0 ? g.Label : "Без группы",
                g.LongestMinutes is null ? "" : "до " + TimeEstimator.Format(g.LongestMinutes),
                g.Items.Select(i => new PlanItemViewModel(
                    i.Def.Name,
                    i.Def.Hidden && HideSpoilers ? "(скрытое)" : i.Def.Description,
                    TimeEstimator.Format(i.EstimateMinutes),
                    i.Def.Percent is { } pc ? pc.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "-",
                    Options.KindTitle(i.Kind))).ToList())).ToList();
            PlanSections.Add(new PlanSectionViewModel($"{Options.StageTitle(s.Stage)} ({s.Count})", groups));
        }

        var (u, m, total) = GameLogic.Progress(_game);
        double steamPct = total == 0 ? 0 : 100.0 * u / total;
        double withManual = total == 0 ? 0 : 100.0 * (u + m) / total;
        ProgressText = $"В Steam открыто {u} из {total} ({steamPct:0}%)" +
                       (m > 0 ? $", ещё {m} сделано, но не засчитано - вместе {withManual:0}%" : "");
        ProgressPercent = steamPct;
        ProgressWithManualPercent = withManual;
        StatUnlocked = $"{u} / {total}";
        StatUnlockedSub = m > 0 ? $"{steamPct:0}% в Steam, +{m} не засчитано" : $"{steamPct:0}% в Steam";
        RemainingText = plan.RemainingMinutes is { } rem
            ? $"До 100% примерно {TimeEstimator.Format(rem)}" + (plan.UnknownEstimateCount > 0 ? $" (без оценки: {plan.UnknownEstimateCount})" : "")
            : _game.Achievements.All(a => a.Percent is null)
                ? "До 100%: нет оценки - Steam не отдал глобальные проценты (нажми \"Обновить схему\")"
                : "До 100%: нет оценки - задай время до 100% или получи пару достижений, тогда оценка появится сама";

        StatRemaining = plan.RemainingMinutes is { } rm ? "~" + TimeEstimator.Format(rm) : "нет оценки";
        StatRemainingSub = plan.RemainingMinutes is null
            ? "задай время до 100% во вкладке \"Время\""
            : _game.CompletionMinutes is not null ? "по времени до 100% " + (_game.CompletionSource == "manual" ? "(вручную)" : "(" + _game.CompletionSource + ")")
            : "оценка по твоему прогрессу";
        var played = TimeEstimator.Format(_game.EffectivePlaytimeMinutes);
        StatPlayed = played;
        StatPlayedSub = _game.PlaytimeOverrideMinutes is not null ? "задано вручную"
            : _game.PlaytimeOffsetMinutes > 0 ? $"из {TimeEstimator.Format(_game.PlaytimeMinutes)} в Steam"
            : "по данным Steam";
        var playedNote = _game.PlaytimeOverrideMinutes is not null ? " (задано вручную)"
            : _game.PlaytimeOffsetMinutes > 0 ? $" (из {TimeEstimator.Format(_game.PlaytimeMinutes)} в Steam)"
            : "";
        CompletionText = (_game.CompletionMinutes is { } cm
            ? $"Время до 100% по игре: {TimeEstimator.Format(cm)} ({(_game.CompletionSource == "manual" ? "вручную" : _game.CompletionSource)}), "
            : _game.DerivedCompletionMinutes is { } dm
                ? $"Время до 100%: примерно {TimeEstimator.Format(dm)} (оценка по твоему прогрессу), "
                : "") + $"наиграно {played}{playedNote}";

        SampleText = _game.SampleFetchedAt is { } sf
            ? $"Выборка: {_game.SamplePlayers} игроков с открытым профилем (авторы обзоров Steam), " +
              (_game.SampleCompletionMinutes is { } sc ? $"на 100% прошли {_game.SampleCompleters}, медиана {TimeEstimator.Format(sc)}" : "на 100% пока никто не прошёл") +
              $". Собрано {sf.ToLocalTime():dd.MM.yyyy}."
            : "Выборка не собиралась. Пригодится, если Steam не отдаёт проценты или нет данных о времени.";
        QueueCount = _game.PendingUnlocks.Count;
        UpdateOverlayLists(all);
    }

    private void UpdateOverlayLists(IReadOnlyList<AchievementRow> all)
    {
        OverlayMissable.Clear();
        foreach (var r in all.Where(r => !r.IsDone && r.Kind == AchievementKind.Missable)
                             .OrderBy(r => r.EstimateMinutes ?? double.MaxValue).Take(6))
            OverlayMissable.Add(new AchievementRowViewModel(this, r));

        OverlayNearest.Clear();
        foreach (var r in all.Where(r => !r.IsDone && r.Kind != AchievementKind.Missable)
                             .OrderBy(r => r.EstimateMinutes is null)
                             .ThenBy(r => r.EstimateMinutes)
                             .ThenByDescending(r => r.Def.Percent ?? -1)
                             .Take(Math.Max(1, _svc.Settings.OverlayNearestCount)))
            OverlayNearest.Add(new AchievementRowViewModel(this, r));
    }

    // ---------- слежение за игрой ----------

    private void RestartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_game is null || _svc.Settings.SteamId64 == 0) return;
        var w = new StatsFileWatcher(_svc.Settings.SteamId64, _game.AppId);
        w.Changed += () => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Чтение через Steam-клиент само может переписать файл - не зацикливаемся.
            if (DateTime.Now - _lastPoll < TimeSpan.FromSeconds(10)) return;
            if (_svc.HasApiKey || _svc.Bridge.Exists) _ = SafeAsync(RefreshStateAsync);
        });
        _watcher = w;
    }

    private async Task OnRunningTickAsync()
    {
        uint running;
        try { running = SteamLocal.RunningAppId(); }
        catch { return; }

        if (running != _lastRunningAppId)
        {
            // Пока что-то грузится - не запоминаем, попробуем на следующем тике.
            if (IsBusy) return;
            _lastRunningAppId = running;
            if (running != 0 && AutoFollow && running != _game?.AppId)
            {
                await SafeAsync(() => LoadAsync(running, Library.FirstOrDefault(g => g.AppId == running)?.Name));
                return;
            }
        }

        // Пока игра идёт - опрашиваем статус (на случай, если файл статистики не поменялся).
        bool canRefresh = _svc.HasApiKey || _svc.Bridge.Exists;
        if (running != 0 && running == _game?.AppId && canRefresh && !IsBusy &&
            DateTime.Now - _lastPoll > TimeSpan.FromSeconds(Math.Max(15, _svc.Settings.PollSeconds)))
        {
            _lastPoll = DateTime.Now;
            await SafeAsync(RefreshStateAsync);
        }
    }

    private void AnnounceNew(IReadOnlyList<string> apiNames)
    {
        if (_game is null || apiNames.Count == 0) return;
        var names = apiNames.Select(n => _game.Achievements.FirstOrDefault(a => a.ApiName == n)?.Name ?? n).ToList();
        var text = "Открыто: " + string.Join(", ", names);
        StatusMessage = text;
        Toast?.Invoke(text);
    }

    // ---------- служебное ----------

    private async Task Busy(string message, Func<Task> action)
    {
        _busyCount++;
        IsBusy = true;
        StatusMessage = message;
        try
        {
            await action();
        }
        finally
        {
            if (--_busyCount == 0) IsBusy = false;
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { OnError(e); }
    }

    private void OnError(Exception e) => StatusMessage = "Ошибка: " + e.Message;

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "game" : s;
    }

    public void Dispose()
    {
        _runningTimer.Stop();
        _watcher?.Dispose();
    }
}
