using Microsoft.EntityFrameworkCore;
using SteamDB.Core;
using SteamDB.Infrastructure.Data;
using SteamDB.Infrastructure.Steam;

namespace SteamDB.Infrastructure;

public interface ISettingsStore
{
    AppSettings Current { get; }
    event Action? Changed;
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default);
}

public interface IDatasetCache
{
    DatasetSnapshot Current { get; }
    event Action? Changed;
    Task ReloadAsync(CancellationToken ct = default);
}

public interface ICollectionController
{
    CollectionStatus Status { get; }
    event Action? Changed;
    ValueTask EnqueueAsync(CollectionProfile profile);
    void RequestStop();
}

public sealed record RunRow(string Profile, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, string? Message, int Requests);
public sealed record ErrorRow(DateTimeOffset At, string Kind, int? StatusCode, string Message);
public sealed record SliceRow(string Key, string Status, int? TotalCount, int PagesFetched, string? Note);

public sealed record CollectionBoard(
    IReadOnlyList<RunRow> Runs,
    IReadOnlyList<ErrorRow> Errors,
    IReadOnlyList<SliceRow> Slices,
    int Pending,
    int Deferred);

public interface ICollectionBoardSource
{
    Task<CollectionBoard> LoadAsync(CancellationToken ct = default);
}

public sealed record LiveCount(int? Total, string? Error, string? Note);

public interface IIdeaSearch
{
    Task<LiveCount> CountAsync(IdeaRequest request, CancellationToken ct = default);
}

public sealed record CalibrationOffer(bool Accepted, string Message, double? UnitsPerReview);

public interface IMyGames
{
    Task<IReadOnlyList<int>> TrackedAsync(CancellationToken ct = default);
    Task AddAsync(int appId, CancellationToken ct = default);
    Task RemoveAsync(int appId, CancellationToken ct = default);
    Task<bool> HasKeyAsync(CancellationToken ct = default);
    Task SaveKeyAsync(string key, CancellationToken ct = default);
    Task ForgetKeyAsync(CancellationToken ct = default);
    Task<CalibrationOffer> CalibrateAsync(CancellationToken ct = default);
    Task ApplyCalibrationAsync(double unitsPerReview, CancellationToken ct = default);
}

public interface ITableExport
{
    byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows);
    byte[] Excel(string sheet, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows);
}

public sealed class SettingsStore(IDbContextFactory<AppDbContext> factory) : ISettingsStore
{
    private readonly object _gate = new();
    private AppSettings _current = new();

    public AppSettings Current
    {
        get { lock (_gate) return _current.Copy(); }
    }

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stored = await db.Settings.FirstOrDefaultAsync(ct);
        if (stored is null)
        {
            stored = new AppSettings();
            db.Settings.Add(stored);
            await db.SaveChangesAsync(ct);
        }

        lock (_gate)
            _current = stored.Copy();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Normalize(settings);
        await using var db = await factory.CreateDbContextAsync(ct);
        var stored = await db.Settings.FirstAsync(ct);
        db.Entry(stored).CurrentValues.SetValues(settings);
        stored.Id = 1;
        await db.SaveChangesAsync(ct);
        lock (_gate)
            _current = stored.Copy();
        Changed?.Invoke();
    }

    public async Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var copy = Current;
        mutate(copy);
        await SaveAsync(copy, ct);
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Id = 1;
        settings.MaxConcurrency = Math.Clamp(settings.MaxConcurrency, 1, 2);
        settings.MinDelayMs = Math.Max(200, settings.MinDelayMs);
        settings.JitterMs = Math.Max(0, settings.JitterMs);
        settings.MaxRetries = Math.Clamp(settings.MaxRetries, 1, 5);
        settings.SuccessReviewThreshold = Math.Max(1, settings.SuccessReviewThreshold);
        settings.MedianMinReviews = Math.Max(0, settings.MedianMinReviews);
        settings.MinSegmentSample = Math.Max(1, settings.MinSegmentSample);
        settings.SmoothingK = Math.Max(0, settings.SmoothingK);
        settings.BayesianM = Math.Max(0, settings.BayesianM);
        settings.ReviewRate = Math.Clamp(settings.ReviewRate, 0.001, 0.5);
        settings.ReviewRateMin = Math.Clamp(settings.ReviewRateMin, 0.001, 0.5);
        settings.ReviewRateMax = Math.Clamp(settings.ReviewRateMax, 0.001, 0.5);
        settings.SearchPageSize = Math.Clamp(settings.SearchPageSize, 10, 100);
        settings.MaxPagesBeforeSplit = Math.Clamp(settings.MaxPagesBeforeSplit, 1, 40);
        settings.QuickTagCount = Math.Clamp(settings.QuickTagCount, 1, 200);
        settings.QuickDetailBudget = Math.Clamp(settings.QuickDetailBudget, 1, 5000);
        settings.DailyRefreshBudget = Math.Clamp(settings.DailyRefreshBudget, 1, 20000);
        settings.DeepSearchPagesPerRun = Math.Clamp(settings.DeepSearchPagesPerRun, 1, 200);
        settings.DeepDetailBudgetPerRun = Math.Clamp(settings.DeepDetailBudgetPerRun, 1, 2000);
    }
}

public sealed class DatasetCache(IDbContextFactory<AppDbContext> factory) : IDatasetCache
{
    public DatasetSnapshot Current { get; private set; } = DatasetSnapshot.Empty;
    public event Action? Changed;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var games = await db.Games.AsNoTracking().ToListAsync(ct);
        var segments = await db.Segments.AsNoTracking().ToListAsync(ct);
        var snapshots = await db.Snapshots.AsNoTracking().ToListAsync(ct);
        var creators = await db.Creators.AsNoTracking().ToListAsync(ct);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AppSettings();
        var history = snapshots.GroupBy(item => item.AppId).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<DailyPoint>)group.OrderBy(item => item.Date).Select(item => new DailyPoint
            {
                AppId = item.AppId,
                Date = item.Date,
                ReviewCount = item.ReviewCount,
                PositiveRatio = item.PositiveRatio,
                CurrentPlayers = item.CurrentPlayers,
                Ranks = JsonWork.Read<List<RankPoint>>(item.RanksJson) ?? []
            }).ToList());
        var followers = creators.ToDictionary(item => item.Name, item => item.Followers, StringComparer.OrdinalIgnoreCase);
        Current = DatasetSnapshot.Create(
            games.Select(Map).ToList(),
            segments.Select(MapSegment).ToList(),
            history,
            settings.DeepCollectionComplete,
            settings.StoreGameCount,
            settings.CalibratedUnitsPerReview,
            followers);
        Changed?.Invoke();
    }

    private static GameRecord Map(GameEntity entity) => new()
    {
        AppId = entity.AppId,
        Name = entity.Name,
        Url = entity.Url,
        HeaderImage = entity.HeaderImage,
        ShortDescription = entity.ShortDescription,
        Developers = JsonWork.Read<List<string>>(entity.DevelopersJson) ?? [],
        Publishers = JsonWork.Read<List<string>>(entity.PublishersJson) ?? [],
        ReviewCount = entity.ReviewCount,
        PositiveReviews = entity.PositiveReviews,
        PositiveRatio = entity.PositiveRatio,
        RecentPositiveRatio = entity.RecentPositiveRatio,
        ReviewsKnown = entity.ReviewsKnown,
        CurrentPlayers = entity.CurrentPlayers,
        PeakPlayers = entity.PeakPlayers,
        ReleaseDate = entity.ReleaseDate,
        ReleaseYear = entity.ReleaseYear,
        PriceCents = entity.PriceCents,
        Currency = entity.Currency,
        DiscountPercent = entity.DiscountPercent,
        IsFree = entity.IsFree,
        Windows = entity.Windows,
        Mac = entity.Mac,
        Linux = entity.Linux,
        Genres = JsonWork.Read<List<string>>(entity.GenresJson) ?? [],
        TagIds = JsonWork.Read<List<int>>(entity.TagIdsJson) ?? [],
        SinglePlayer = entity.SinglePlayer,
        Coop = entity.Coop,
        Multiplayer = entity.Multiplayer,
        Vr = entity.Vr,
        Controller = entity.Controller,
        Achievements = entity.Achievements,
        Cloud = entity.Cloud,
        Workshop = entity.Workshop,
        HasDemo = entity.HasDemo,
        Status = (ReleaseStatus)entity.Status,
        Deck = (DeckStatus)entity.Deck,
        LanguageCount = entity.LanguageCount,
        ScreenshotCount = entity.ScreenshotCount,
        HasTrailer = entity.HasTrailer,
        IsAdult = entity.IsAdult,
        CardRead = entity.CardRead,
        PublicCardUnavailable = entity.PublicCardUnavailable
    };

    private static SegmentRecord MapSegment(SegmentEntity entity) => new()
    {
        Id = entity.Id,
        Slug = entity.Slug,
        Name = entity.Name,
        Kind = (SegmentKind)entity.Kind,
        SteamTagId = entity.SteamTagId,
        TotalCount = entity.TotalCount,
        PopularAppIds = JsonWork.Read<List<int>>(entity.PopularJson) ?? [],
        SizeHistory = JsonWork.Read<List<SegmentSizePoint>>(entity.HistoryJson) ?? []
    };
}
