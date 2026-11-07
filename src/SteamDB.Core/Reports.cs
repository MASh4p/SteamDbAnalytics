namespace SteamDB.Core;

public sealed record ProportionView(int Successes, int Total, double Point, double Lower, double Upper)
{
    public static ProportionView? Create(int successes, int total)
    {
        if (total <= 0)
            return null;
        var wilson = WilsonInterval.FromCounts(successes, total);
        return new ProportionView(successes, total, wilson.Point, wilson.Lower, wilson.Upper);
    }
}

public sealed record SteamBaseline(
    int Collected,
    int? StoreCount,
    double? ShareWithoutReviews,
    double? MedianReviews,
    double? P90,
    double? GeometricMean,
    ProportionView? Chance,
    double AveragePositiveRatio);

public sealed record SegmentMetrics
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public SegmentKind Kind { get; init; }
    public int? SteamTagId { get; init; }
    public int? StoreCount { get; init; }
    public int Collected { get; init; }
    public int WithReviews { get; init; }
    public int MedianSample { get; init; }
    public double? MedianReviews { get; init; }
    public double? SmoothedMedian { get; init; }
    public double? P25 { get; init; }
    public double? P75 { get; init; }
    public double? P90 { get; init; }
    public ProportionView? Chance { get; init; }
    public double? Trend { get; init; }
    public double? EntryThreshold { get; init; }
    public double? Demand { get; init; }
    public double? CompetitionSize { get; init; }
    public bool SmallSample { get; init; }
    public int? SizeGrowth { get; init; }
}

public sealed record ScoredSegment(
    SegmentMetrics Metrics,
    double DemandPercentile,
    double SuccessPercentile,
    double CompetitionPercentile,
    double TrendPercentile,
    double Opportunity,
    QuadrantKind Quadrant);

public sealed record YearCohort(int Year, int Games, double? MedianReviews);

public sealed record BucketCount(string Label, int Count);

public sealed record HotGame(GameRecord Game, double? Score);

public sealed record OverviewReport(
    SteamBaseline Steam,
    IReadOnlyList<BucketCount> ReviewBuckets,
    IReadOnlyList<YearCohort> Cohorts,
    IReadOnlyList<ScoredSegment> TopNiches,
    IReadOnlyList<GameRecord> MostReviewed,
    IReadOnlyList<HotGame> Hottest,
    bool HotAvailable,
    bool SuccessInflated,
    IReadOnlyList<string> Conclusions);

public sealed record TagPosition(string Tag, int Rank, IReadOnlyList<GameRecord> Neighbors);

public sealed record GameInsight(
    GameRecord Game,
    double? Quality,
    double? BetterThan,
    double? Hot,
    int? ReviewGrowth,
    ReachEstimate Reach,
    IReadOnlyList<DailyPoint> History,
    IReadOnlyList<TagPosition> Positions,
    IReadOnlyList<GameRecord> Similar);

public sealed record PairReport(
    int TagIdA,
    string NameA,
    int TagIdB,
    string NameB,
    int Count,
    double? MedianPair,
    double? MedianA,
    double? MedianB,
    double? Lift,
    ProportionView? Chance,
    bool SmallSample);

public sealed record TagAdvice(string Tag, int TagId, double? MedianWith, double? MedianWithout, int WithCount, int WithoutCount);

public sealed record IdeaReport(
    int Matched,
    int EstimatedCompetitors,
    double? Median,
    double? P75,
    double? P90,
    IReadOnlyList<(int Threshold, ProportionView? Chance, ProportionView? Steam)> Chances,
    ReachEstimate TypicalOwners,
    ReachEstimate TopOwners,
    IReadOnlyList<TagAdvice> Add,
    IReadOnlyList<TagAdvice> Avoid);

public sealed record IdeaRequest
{
    public IReadOnlyList<int> TagIds { get; init; } = [];
    public string? Genre { get; init; }
    public string? Platform { get; init; }
    public string? PriceShelfKey { get; init; }
    public DeckStatus? Deck { get; init; }
    public bool? Multiplayer { get; init; }
    public int NeighborMin { get; init; } = 8;
}

public sealed record FactorRow(
    string Name,
    string Group,
    int WithCount,
    int WithoutCount,
    double? MedianWith,
    double? MedianWithout,
    ProportionView? ChanceWith,
    ProportionView? ChanceWithout,
    double? Uplift,
    double? UpliftLow,
    double? UpliftHigh,
    double? Multiplier,
    double? MultiplierLow,
    double? MultiplierHigh,
    bool Starred,
    double? PValue);

public sealed record SpearmanRow(string Name, double? Rho);

public sealed record FactorReport(
    int SampleSize,
    bool ModelReady,
    double? RSquared,
    double? AgeDoubling,
    double? ScreenshotDoubling,
    IReadOnlyList<FactorRow> Rows,
    IReadOnlyList<SpearmanRow> Correlations);

public sealed record FormatRow(string Group, string Name, SegmentMetrics Metrics, double? ChanceRatio);

public sealed record CreatorReport(
    string Name,
    int? Followers,
    int GameCount,
    int TotalReviews,
    double? MedianReviews,
    string? BestGame,
    int? BestAppId,
    int? ReviewGrowth30,
    int? PlayerGrowth30);

public sealed record ExperienceBand(string Label, int Authors, double? MedianReviews);

public sealed record CreatorSummary(
    int Authors,
    int SingleGameAuthors,
    double? TopOnePercentShare,
    double? MedianAudienceReviews,
    IReadOnlyList<ExperienceBand> Experience,
    IReadOnlyList<CreatorReport> Rows);

public sealed record GameQuery
{
    public string? Text { get; init; }
    public string? Genre { get; init; }
    public int? TagId { get; init; }
    public string? Platform { get; init; }
    public string? PriceShelfKey { get; init; }
    public int? Year { get; init; }
    public DeckStatus? Deck { get; init; }
    public bool EarlyAccessOnly { get; init; }
    public GameSort Sort { get; init; } = GameSort.MostReviews;
}
