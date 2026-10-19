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

    private bool Spoiler => _row.Def.Hidden && !_row.IsDone && _owner.HideSpoilers && !_revealed;

    private bool _revealed;
    /// <summary>Пользователь попросил показать описание скрытого достижения.</summary>
    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (!Set(ref _revealed, value)) return;
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(IsSpoilerHidden));
        }
    }

    /// <summary>Описание спрятано (секретное, не получено, спойлеры скрываются).</summary>
    public bool IsSpoilerHidden => Spoiler;

    public bool IsHidden => _row.Def.Hidden;
    public bool IsLocked => _row.Status == EffectiveStatus.Locked;
    public bool IsManualStatus => _row.Status == EffectiveStatus.ManualDone;
    public bool IsPendingWrite => _owner.IsPending(ApiName);
    public string KindTitle => Options.KindTitle(_kind);
    public bool HasKind => _kind != AchievementKind.Unknown;

    /// <summary>Подпись редкости по глобальному проценту.</summary>
    public string RarityText => _row.Def.GlobalPercent switch
    {
        null => "",
        < 5 => "очень редкое",
        < 20 => "редкое",
        < 50 => "необычное",
        _ => "частое",
    };

    public string Description =>
        Spoiler ? "Скрытое достижение. Нажми \"Показать\" в карточке справа или наведи курсор" :
        _row.Def.Description.Length > 0 ? _row.Def.Description :
        _row.Def.Hidden ? "Скрытое достижение (описание Steam не отдал)" : "";

    public string Tooltip => _row.Def.Description.Length > 0 ? _row.Def.Description : _row.ApiName;

    /// <summary>Для оверлея: описание всегда видно, скрытые помечены.</summary>
    public string OverlayDescription =>
        (_row.Def.Hidden ? "[секрет] " : "") +
        (_row.Def.Description.Length > 0 ? _row.Def.Description : _row.Def.Hidden ? "описание Steam не отдал" : "");

    /// <summary>Можно записать в Steam: ещё не открыто и не защищено.</summary>
    public bool CanWriteToSteam => !IsUnlocked && !IsProtected;

    public string? Icon
    {
        get
        {
            var url = _row.IsDone ? _row.Def.IconUrl : _row.Def.IconGrayUrl;
            if (string.IsNullOrEmpty(url)) url = _row.Def.IconUrl;
            return string.IsNullOrEmpty(url) ? null : url;
        }
    }

    public string Percent => _row.Def.GlobalPercent is { } p ? p.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "-";
    public double PercentValue => _row.Def.GlobalPercent ?? -1;

    public string Estimate => TimeEstimator.Format(_row.EstimateMinutes) + (_row.EstimateIsManual ? " *" : "");

    public string Badges =>
        string.Join(" ", new[]
        {
            _row.Def.Hidden ? "секрет" : null,
            _row.Def.Protected ? "защищено" : null,
            _owner.IsPending(ApiName) ? "в очереди" : null,
        }.Where(s => s != null));

    public string StatusText => _row.Status switch
    {
        EffectiveStatus.Unlocked => _row.UnlockTime is { } t ? "Открыто " + t.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) : "Открыто",
        EffectiveStatus.ManualDone => "Сделано, не засчитано",
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
                    _owner.StatusMessage = $"\"{value}\" - не число часов, оценка не изменена";
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
        nameof(Description), nameof(Tooltip), nameof(Icon), nameof(Percent), nameof(PercentValue),
        nameof(Estimate), nameof(Badges), nameof(StatusText), nameof(ManualDone), nameof(OverlayDescription),
        nameof(CanWriteToSteam), nameof(IsSpoilerHidden), nameof(IsHidden), nameof(IsLocked), nameof(IsManualStatus),
        nameof(IsPendingWrite), nameof(RarityText),
    };

    /// <summary>Подменяет данные строки после пересчёта, не пересоздавая объект (чтобы не терять фокус).</summary>
    public void Update(AchievementRow row)
    {
        _row = row;
        _manualDone = row.Status == EffectiveStatus.ManualDone;
        foreach (var n in Derived) OnPropertyChanged(n);
    }
}
