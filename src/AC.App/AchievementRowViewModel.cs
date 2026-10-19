using System.Globalization;
using AC.Core;

namespace AC.App;

/// <summary>Строка таблицы достижений. Правки сразу уходят в MainViewModel (и на диск).</summary>
public sealed class AchievementRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private AchievementRow _row;
    private bool _manualDone;
    private AchievementKind _kind;
    private string _group;
    private string _note;
    private string _estimateInput;

    public AchievementRowViewModel(MainViewModel owner, AchievementRow row)
    {
        _owner = owner;
        _row = row;
        RevealCommand = new RelayCommand(() => _owner.Reveal(ApiName));
        _manualDone = row.Status == EffectiveStatus.ManualDone;
        _kind = row.Kind;
        _group = row.Group;
        _note = row.Note;
        _estimateInput = row.EstimateIsManual && row.EstimateMinutes.HasValue
            ? (row.EstimateMinutes.Value / 60.0).ToString("0.##", CultureInfo.CurrentCulture)
            : "";
    }

    public string ApiName => _row.ApiName;
    public AchievementDef Def => _row.Def;
    public EffectiveStatus Status => _row.Status;
    public bool IsDone => _row.IsDone;
    public bool IsUnlocked => _row.Status == EffectiveStatus.Unlocked;
    public bool IsProtected => _row.Def.Protected;

    public string Name => _row.Def.Name.Length > 0 ? _row.Def.Name : _row.ApiName;

    /// <summary>Описание секретного неполученного достижения размыто, пока его не открыли кликом.</summary>
    public bool IsSpoilerHidden =>
        _row.Def.Hidden && !_row.IsDone && _row.Def.Description.Length > 0 && _owner.HideSpoilers && !_owner.IsRevealed(ApiName);

    public RelayCommand RevealCommand { get; }

    public void RefreshSpoiler() => OnPropertyChanged(nameof(IsSpoilerHidden));

    public bool IsHidden => _row.Def.Hidden;
    public bool IsLocked => _row.Status == EffectiveStatus.Locked;
    public bool IsManualStatus => _row.Status == EffectiveStatus.ManualDone;
    public bool GroupIsAuto => _row.GroupIsAuto;
    public bool IsPendingWrite => _owner.IsPending(ApiName);
    public string KindTitle => Options.KindTitle(_kind);
    public bool HasKind => _kind != AchievementKind.Unknown;

    /// <summary>Подпись редкости по глобальному проценту.</summary>
    public string RarityText
    {
        get
        {
            if (_row.Def.Percent is not { } p) return "";
            var rarity = Loc.T(p switch
            {
                < 5 => "rarity.veryRare",
                < 20 => "rarity.rare",
                < 50 => "rarity.uncommon",
                _ => "rarity.common",
            });
            return _row.Def.PercentFromSample ? Loc.T("rarity.fromSample", rarity) : rarity;
        }
    }

    /// <summary>0 - нет данных, 1 - частое, 2 - необычное, 3 - редкое, 4 - очень редкое. Для цвета.</summary>
    public int RarityLevel => _row.Def.Percent switch
    {
        null => 0,
        < 5 => 4,
        < 20 => 3,
        < 50 => 2,
        _ => 1,
    };

    /// <summary>Откуда оценка времени - для подсказки.</summary>
    public string EstimateHint
    {
        get
        {
            if (_row.EstimateIsManual) return Loc.T("estimate.own");
            var est = _owner.Game?.Estimates.GetValueOrDefault(ApiName);
            return est?.Source switch
            {
                AC.Core.SampleStats.SourceName => Loc.T("estimate.sample", est.SampleSize),
                AC.Core.TimeEstimator.SourceName => Loc.T("estimate.model"),
                _ => Loc.T("estimate.none"),
            };
        }
    }

    public string Description =>
        _row.Def.Description.Length > 0 ? _row.Def.Description :
        _row.Def.Hidden ? Loc.T("card.noDescription") : "";

    /// <summary>Для оверлея: описание всегда видно, скрытые помечены.</summary>
    public string OverlayDescription
    {
        get
        {
            var text = _row.Def.Description.Length > 0 ? _row.Def.Description
                : _row.Def.Hidden ? Loc.T("overlay.noDescription") : "";
            return _row.Def.Hidden ? Loc.T("overlay.hidden", text) : text;
        }
    }

    /// <summary>Можно записать в Steam: запись включена в настройках, достижение ещё не открыто и не защищено.</summary>
    public bool CanWriteToSteam => _owner.ManualUnlocks && !IsUnlocked && !IsProtected;

    public string? Icon
    {
        get
        {
            var url = _row.IsDone ? _row.Def.IconUrl : _row.Def.IconGrayUrl;
            if (string.IsNullOrEmpty(url)) url = _row.Def.IconUrl;
            return string.IsNullOrEmpty(url) ? null : url;
        }
    }

    public string Percent => _row.Def.Percent is { } p ? p.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "-";
    public double PercentValue => _row.Def.Percent ?? -1;

    public string Estimate => TimeEstimator.Format(_row.EstimateMinutes) + (_row.EstimateIsManual ? " *" : "");

    public string StatusText => _row.Status switch
    {
        EffectiveStatus.Unlocked => _row.UnlockTime is { } t
            ? Loc.T("card.unlockedAt", t.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture))
            : Loc.T("card.unlocked"),
        EffectiveStatus.ManualDone => Loc.T("card.manualDone"),
        _ => "",
    };

    /// <summary>Галочка "выполнено, но не засчиталось". Для открытых в Steam недоступна.</summary>
    public bool ManualDone
    {
        get => IsUnlocked || _manualDone;
        set
        {
            if (IsUnlocked) return;
            if (!Set(ref _manualDone, value)) return;
            _owner.OnManualDoneChanged(this, value);
        }
    }

    public AchievementKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            OnPropertyChanged(nameof(KindTitle));
            OnPropertyChanged(nameof(HasKind));
            _owner.OnKindChanged(this, value);
        }
    }

    public string Group
    {
        get => _group;
        set
        {
            value ??= "";
            if (!Set(ref _group, value)) return;
            _owner.OnGroupChanged(this, value);
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            value ??= "";
            if (!Set(ref _note, value)) return;
            _owner.OnNoteChanged(this, value);
        }
    }

    /// <summary>Ручная оценка в часах, пусто - автоматическая.</summary>
    public string EstimateInput
    {
        get => _estimateInput;
        set
        {
            value ??= "";
            if (!Set(ref _estimateInput, value)) return;
            double? minutes = null;
            var s = value.Trim().Replace(',', '.');
            if (s.Length > 0)
            {
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h <= 0)
                {
                    _owner.StatusMessage = Loc.T("status.estimateBad", value);
                    return;
                }
                minutes = h * 60;
            }
            _owner.OnEstimateChanged(this, minutes);
        }
    }

    // Только вычисляемые свойства: поля ввода (Kind, Group, Note, EstimateInput) не трогаем,
    // иначе фоновое обновление стирает то, что пользователь сейчас печатает.
    private static readonly string[] Derived =
    {
        nameof(Def), nameof(Status), nameof(IsDone), nameof(IsUnlocked), nameof(IsProtected), nameof(Name),
        nameof(Description), nameof(Icon), nameof(Percent), nameof(PercentValue),
        nameof(Estimate), nameof(StatusText), nameof(ManualDone), nameof(OverlayDescription),
        nameof(CanWriteToSteam), nameof(IsSpoilerHidden), nameof(IsHidden), nameof(IsLocked), nameof(IsManualStatus),
        nameof(GroupIsAuto), nameof(IsPendingWrite), nameof(RarityText), nameof(RarityLevel), nameof(EstimateHint),
    };

    /// <summary>Подменяет данные строки после пересчёта, не пересоздавая объект (чтобы не терять фокус).</summary>
    public void Update(AchievementRow row)
    {
        _row = row;
        _manualDone = row.Status == EffectiveStatus.ManualDone;
        foreach (var n in Derived) OnPropertyChanged(n);
    }
}
