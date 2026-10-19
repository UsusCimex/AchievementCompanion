using System.Windows;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>A library game: playtime and progress from the summary (only games opened in the app have one).</summary>
public sealed class LibraryItemViewModel : ObservableObject
{
    private GameSummary? _summary;

    public LibraryItemViewModel(OwnedGame game, GameSummary? summary)
    {
        Game = game;
        _summary = summary;
    }

    public OwnedGame Game { get; }
    public uint AppId => Game.AppId;
    public string Name => Game.Name;
    public string HeaderUrl => Game.HeaderUrl;

    public string PlaytimeText => Game.PlaytimeMinutes > 0
        ? Loc.T("library.played", TimeEstimator.Format(Game.PlaytimeMinutes))
        : Loc.T("library.neverPlayed");

    public void RefreshTexts() => OnPropertyChanged(nameof(PlaytimeText));

    public GameSummary? Summary
    {
        get => _summary;
        set
        {
            if (!Set(ref _summary, value)) return;
            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(IsPerfect));
            OnPropertyChanged(nameof(ShowProgressText));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(DoneStars));
            OnPropertyChanged(nameof(RestStars));
        }
    }

    public bool HasProgress => _summary is { Total: > 0 };
    public bool IsPerfect => _summary is { Total: > 0 } s && s.Unlocked >= s.Total;
    public bool ShowProgressText => HasProgress && !IsPerfect;
    public string ProgressText => _summary is { Total: > 0 } s ? $"{s.Unlocked}/{s.Total}" : "";

    // Widths of the progress bar parts (Grid star columns).
    public GridLength DoneStars => new(_summary?.Unlocked ?? 0, GridUnitType.Star);
    public GridLength RestStars => new(_summary is { } s ? Math.Max(0, s.Total - s.Unlocked) : 1, GridUnitType.Star);
}
