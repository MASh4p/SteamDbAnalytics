namespace SteamDB.Core;

public enum ReleaseStatus
{
    Released = 0,
    EarlyAccess = 1,
    Announced = 2
}

public enum DeckStatus
{
    Unknown = 0,
    Unsupported = 1,
    Playable = 2,
    Verified = 3
}

public enum SegmentKind
{
    Genre = 0,
    Tag = 1,
    Category = 2,
    Platform = 3,
    Format = 4,
    Language = 5
}

public enum QuadrantKind
{
    BlueOcean,
    RedOcean,
    QuietNiche,
    Trap
}

public enum SliceStatus
{
    Pending,
    Partial,
    Complete,
    Empty,
    Split,
    Deferred
}

public enum CollectionProfile
{
    QuickStart,
    Daily,
    Deep,
    CardBackfill
}

public enum RunState
{
    Idle,
    Running,
    Stopped,
    Completed,
    Failed
}

public enum GameSort
{
    MostReviews,
    ReviewQuality,
    Hot,
    ReviewGrowth,
    Newest,
    Online
}

public sealed record GameRecord
{
    public int AppId { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? HeaderImage { get; init; }
    public string? ShortDescription { get; init; }
    public IReadOnlyList<string> Developers { get; init; } = [];
    public IReadOnlyList<string> Publishers { get; init; } = [];
    public int? ReviewCount { get; init; }
    public int? PositiveReviews { get; init; }
    public double? PositiveRatio { get; init; }
    public double? RecentPositiveRatio { get; init; }
    public bool ReviewsKnown { get; init; }
    public int? CurrentPlayers { get; init; }
    public int? PeakPlayers { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? ReleaseYear { get; init; }
    public int? PriceCents { get; init; }
    public string? Currency { get; init; }
    public int DiscountPercent { get; init; }
    public bool IsFree { get; init; }
    public bool Windows { get; init; }
    public bool Mac { get; init; }
    public bool Linux { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<int> TagIds { get; init; } = [];
    public bool SinglePlayer { get; init; }
    public bool Coop { get; init; }
    public bool Multiplayer { get; init; }
    public bool Vr { get; init; }
    public bool Controller { get; init; }
    public bool Achievements { get; init; }
    public bool Cloud { get; init; }
    public bool Workshop { get; init; }
    public bool HasDemo { get; init; }
    public ReleaseStatus Status { get; init; }
    public DeckStatus Deck { get; init; }
    public int? LanguageCount { get; init; }
    public int ScreenshotCount { get; init; }
    public bool HasTrailer { get; init; }
    public bool IsAdult { get; init; }
    public bool CardRead { get; init; }
    public bool PublicCardUnavailable { get; init; }
}

public sealed record RankPoint(int SegmentId, int Rank);

public sealed record DailyPoint
{
    public int AppId { get; init; }
    public DateOnly Date { get; init; }
    public int? ReviewCount { get; init; }
    public double? PositiveRatio { get; init; }
    public int? CurrentPlayers { get; init; }
    public IReadOnlyList<RankPoint> Ranks { get; init; } = [];
}

public sealed record SegmentSizePoint(DateOnly Date, int TotalCount);

public sealed record SegmentRecord
{
    public int Id { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public SegmentKind Kind { get; init; }
    public int? SteamTagId { get; init; }
    public int? TotalCount { get; init; }
    public IReadOnlyList<int> PopularAppIds { get; init; } = [];
    public IReadOnlyList<SegmentSizePoint> SizeHistory { get; init; } = [];
}

public sealed class AppSettings
{
    public int Id { get; set; } = 1;
    public int MinDelayMs { get; set; } = 1200;
    public int JitterMs { get; set; } = 400;
    public int MaxConcurrency { get; set; } = 1;
    public int MaxRetries { get; set; } = 3;
    public int SuccessReviewThreshold { get; set; } = 500;
    public int MedianMinReviews { get; set; } = 50;
    public bool HideAdult { get; set; } = true;
    public bool IncludeGamesWithoutReviews { get; set; }
    public int MinSegmentSample { get; set; } = 30;
    public int SmoothingK { get; set; } = 20;
    public int BayesianM { get; set; } = 50;
    public double WeightDemand { get; set; } = 0.35;
    public double WeightSuccess { get; set; } = 0.25;
    public double WeightCompetition { get; set; } = 0.25;
    public double WeightTrend { get; set; } = 0.15;
    public double HotOnlineWeight { get; set; } = 0.15;
    public double HotPositionWeight { get; set; } = 1;
    public double HotAgeExponent { get; set; } = 0.5;
    public double ReviewRate { get; set; } = 0.02;
    public double ReviewRateMin { get; set; } = 0.01;
    public double ReviewRateMax { get; set; } = 0.05;
    public double? CalibratedUnitsPerReview { get; set; }
    public int QuickTagCount { get; set; } = 20;
    public int QuickPagesPerList { get; set; } = 1;
    public int QuickDetailBudget { get; set; } = 100;
    public int DailyRefreshBudget { get; set; } = 400;
    public int DeepSearchPagesPerRun { get; set; } = 10;
    public int DeepDetailBudgetPerRun { get; set; } = 40;
    public int DeepMinSegmentSize { get; set; } = 200;
    public int DeepStartYear { get; set; } = 2018;
    public int SearchPageSize { get; set; } = 50;
    public int MaxPagesBeforeSplit { get; set; } = 10;
    public bool DeepCollectionComplete { get; set; }
    public int? StoreGameCount { get; set; }
    public DateTimeOffset? LastDailyRunUtc { get; set; }

    public AppSettings Copy() => new()
    {
        Id = Id,
        MinDelayMs = MinDelayMs,
        JitterMs = JitterMs,
        MaxConcurrency = MaxConcurrency,
        MaxRetries = MaxRetries,
        SuccessReviewThreshold = SuccessReviewThreshold,
        MedianMinReviews = MedianMinReviews,
        HideAdult = HideAdult,
        IncludeGamesWithoutReviews = IncludeGamesWithoutReviews,
        MinSegmentSample = MinSegmentSample,
        SmoothingK = SmoothingK,
        BayesianM = BayesianM,
        WeightDemand = WeightDemand,
        WeightSuccess = WeightSuccess,
        WeightCompetition = WeightCompetition,
        WeightTrend = WeightTrend,
        HotOnlineWeight = HotOnlineWeight,
        HotPositionWeight = HotPositionWeight,
        HotAgeExponent = HotAgeExponent,
        ReviewRate = ReviewRate,
        ReviewRateMin = ReviewRateMin,
        ReviewRateMax = ReviewRateMax,
        CalibratedUnitsPerReview = CalibratedUnitsPerReview,
        QuickTagCount = QuickTagCount,
        QuickPagesPerList = QuickPagesPerList,
        QuickDetailBudget = QuickDetailBudget,
        DailyRefreshBudget = DailyRefreshBudget,
        DeepSearchPagesPerRun = DeepSearchPagesPerRun,
        DeepDetailBudgetPerRun = DeepDetailBudgetPerRun,
        DeepMinSegmentSize = DeepMinSegmentSize,
        DeepStartYear = DeepStartYear,
        SearchPageSize = SearchPageSize,
        MaxPagesBeforeSplit = MaxPagesBeforeSplit,
        DeepCollectionComplete = DeepCollectionComplete,
        StoreGameCount = StoreGameCount,
        LastDailyRunUtc = LastDailyRunUtc
    };

    public AnalysisWindow ToWindow() => new()
    {
        HideAdult = HideAdult,
        MedianMinReviews = Math.Max(0, MedianMinReviews),
        IncludeWithoutReviews = IncludeGamesWithoutReviews,
        SuccessThreshold = Math.Max(1, SuccessReviewThreshold),
        SmoothingK = Math.Max(0, SmoothingK),
        BayesianM = Math.Max(0, BayesianM),
        MinSegmentSample = Math.Max(1, MinSegmentSample),
        WeightDemand = WeightDemand,
        WeightSuccess = WeightSuccess,
        WeightCompetition = WeightCompetition,
        WeightTrend = WeightTrend,
        HotOnlineWeight = HotOnlineWeight,
        HotPositionWeight = HotPositionWeight,
        HotAgeExponent = HotAgeExponent,
        ReviewRate = ReviewRate,
        ReviewRateMin = ReviewRateMin,
        ReviewRateMax = ReviewRateMax,
        CalibratedUnitsPerReview = CalibratedUnitsPerReview
    };
}

public sealed record AnalysisWindow
{
    public bool HideAdult { get; init; } = true;
    public int MedianMinReviews { get; init; } = 50;
    public bool IncludeWithoutReviews { get; init; }
    public int SuccessThreshold { get; init; } = 500;
    public int SmoothingK { get; init; } = 20;
    public int BayesianM { get; init; } = 50;
    public int MinSegmentSample { get; init; } = 30;
    public double WeightDemand { get; init; } = 0.35;
    public double WeightSuccess { get; init; } = 0.25;
    public double WeightCompetition { get; init; } = 0.25;
    public double WeightTrend { get; init; } = 0.15;
    public double HotOnlineWeight { get; init; } = 0.15;
    public double HotPositionWeight { get; init; } = 1;
    public double HotAgeExponent { get; init; } = 0.5;
    public double ReviewRate { get; init; } = 0.02;
    public double ReviewRateMin { get; init; } = 0.01;
    public double ReviewRateMax { get; init; } = 0.05;
    public double? CalibratedUnitsPerReview { get; init; }
}

public sealed class DatasetSnapshot
{
    public static DatasetSnapshot Empty { get; } = new();

    public IReadOnlyList<GameRecord> Games { get; init; } = [];
    public IReadOnlyDictionary<int, GameRecord> ByAppId { get; init; } = new Dictionary<int, GameRecord>();
    public IReadOnlyList<SegmentRecord> Segments { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<DailyPoint>> History { get; init; }
        = new Dictionary<int, IReadOnlyList<DailyPoint>>();
    public IReadOnlyDictionary<string, int?> CreatorFollowers { get; init; }
        = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
    public bool DeepCollectionComplete { get; init; }
    public int? StoreGameCount { get; init; }
    public double? CalibratedUnitsPerReview { get; init; }
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool HasTwoSnapshotDays =>
        History.Values.SelectMany(static points => points).Select(static point => point.Date).Distinct().Count() >= 2;

    public static DatasetSnapshot Create(
        IReadOnlyList<GameRecord> games,
        IReadOnlyList<SegmentRecord> segments,
        IReadOnlyDictionary<int, IReadOnlyList<DailyPoint>> history,
        bool deepComplete,
        int? storeGameCount,
        double? calibratedUnitsPerReview,
        IReadOnlyDictionary<string, int?>? followers = null) => new()
    {
        Games = games,
        ByAppId = games.ToDictionary(static game => game.AppId),
        Segments = segments,
        History = history,
        DeepCollectionComplete = deepComplete,
        StoreGameCount = storeGameCount,
        CalibratedUnitsPerReview = calibratedUnitsPerReview,
        CreatorFollowers = followers ?? new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase),
        LoadedAt = DateTimeOffset.UtcNow
    };
}

public sealed record CollectionStatus
{
    public static CollectionStatus Idle { get; } = new()
    {
        Title = "Сбор не запущен"
    };

    public RunState State { get; init; } = RunState.Idle;
    public CollectionProfile? Profile { get; init; }
    public string Title { get; init; } = "Сбор не запущен";
    public string? Detail { get; init; }
    public int Done { get; init; }
    public int? Planned { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}

public static class Labels
{
    public const string Bias =
        "Приблизительно и завышено: глубокий сбор не закончен, в базе сначала оказываются заметные игры.";

    public const string HotEmpty =
        "Горячее и приросты пустые: нужны собственные снимки минимум за два разных дня.";

    public const string CorrelationNote =
        "Это сопутствие, не причина. Признак встречается вместе с числом отзывов, но отсюда не следует, что он их создаёт.";

    public static string Quadrant(QuadrantKind kind) => kind switch
    {
        QuadrantKind.BlueOcean => "Голубой океан",
        QuadrantKind.RedOcean => "Красный океан",
        QuadrantKind.QuietNiche => "Тихая ниша",
        QuadrantKind.Trap => "Ловушка",
        _ => "—"
    };

    public static string Kind(SegmentKind kind) => kind switch
    {
        SegmentKind.Genre => "Жанр",
        SegmentKind.Tag => "Тег",
        SegmentKind.Category => "Категория",
        SegmentKind.Platform => "Платформа",
        SegmentKind.Format => "Формат",
        SegmentKind.Language => "Язык",
        _ => "Сегмент"
    };

    public static string Status(ReleaseStatus status) => status switch
    {
        ReleaseStatus.EarlyAccess => "Ранний доступ",
        ReleaseStatus.Announced => "Анонс",
        _ => "Вышла"
    };

    public static string Deck(DeckStatus deck) => deck switch
    {
        DeckStatus.Verified => "Проверена",
        DeckStatus.Playable => "Играбельна",
        DeckStatus.Unsupported => "Не поддерживается",
        _ => "Неизвестно"
    };

    public static string Profile(CollectionProfile profile) => profile switch
    {
        CollectionProfile.QuickStart => "Быстрый старт",
        CollectionProfile.Daily => "Ежедневное обновление",
        CollectionProfile.Deep => "Глубокий сбор",
        CollectionProfile.CardBackfill => "Дочитка карточек",
        _ => profile.ToString()
    };
}
