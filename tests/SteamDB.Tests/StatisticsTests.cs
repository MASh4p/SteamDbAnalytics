using SteamDB.Core;

namespace SteamDB.Tests;

public class StatisticsTests
{
    [Fact]
    public void Median_and_percentiles_use_sorted_values()
    {
        Assert.Equal(2, Statistics.Median([1, 3, 2]));
        Assert.Equal(2.5, Statistics.Median([1, 2, 3, 4]));
        Assert.Null(Statistics.Median([]));
        Assert.Equal(1, Statistics.Percentile([1, 2, 3, 4], 0));
        Assert.Equal(4, Statistics.Percentile([1, 2, 3, 4], 1));
    }

    [Fact]
    public void Wilson_interval_contains_the_point_and_stays_inside_unit_interval()
    {
        var interval = WilsonInterval.FromCounts(80, 100);
        Assert.InRange(interval.Lower, 0.70, 0.73);
        Assert.InRange(interval.Upper, 0.85, 0.88);
        Assert.True(interval.Lower < interval.Point && interval.Point < interval.Upper);
        var certain = WilsonInterval.FromCounts(0, 10);
        Assert.Equal(0, certain.Lower);
        Assert.True(certain.Upper > 0);
    }

    [Fact]
    public void Smoothing_pulls_a_small_sample_toward_the_prior()
    {
        Assert.Equal(60, Statistics.Smooth(100, 10, 40, 20));
    }

    [Fact]
    public void Opportunity_and_quadrants_follow_the_weights()
    {
        var score = Statistics.Opportunity(1, 1, 0, 1, 0.35, 0.25, 0.25, 0.15);
        Assert.Equal(100, score, 6);
        Assert.Equal(QuadrantKind.BlueOcean, Statistics.Quadrant(10, 1, 5, 5));
        Assert.Equal(QuadrantKind.RedOcean, Statistics.Quadrant(10, 9, 5, 5));
        Assert.Equal(QuadrantKind.QuietNiche, Statistics.Quadrant(1, 1, 5, 5));
        Assert.Equal(QuadrantKind.Trap, Statistics.Quadrant(1, 9, 5, 5));
    }

    [Fact]
    public void Trend_compares_shares()
    {
        Assert.Equal(1, Statistics.Trend(2, 4, 1, 4));
        Assert.Null(Statistics.Trend(2, 4, 0, 4));
    }

    [Fact]
    public void Lift_and_uplift_are_ratios()
    {
        Assert.Equal(2, Statistics.PairLift(100, 50, 40));
        var uplift = Statistics.Uplift(50, 100, 25, 100);
        Assert.Equal(2, uplift.Ratio);
        Assert.NotNull(uplift.Low);
        Assert.True(uplift.Low < uplift.Ratio && uplift.Ratio < uplift.High);
    }

    [Fact]
    public void Hot_score_needs_two_dates()
    {
        var oneDay = new DailyPoint { AppId = 1, Date = new DateOnly(2026, 10, 1), ReviewCount = 10, CurrentPlayers = 3 };
        Assert.Null(Statistics.Hot([oneDay], new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 5), 0.15, 1, 0.5));
        var next = oneDay with { Date = new DateOnly(2026, 10, 3), ReviewCount = 20, CurrentPlayers = 5 };
        Assert.NotNull(Statistics.Hot([oneDay, next], new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 5), 0.15, 1, 0.5));
    }

    [Fact]
    public void Ordinary_least_squares_recovers_a_line()
    {
        var design = new double[8][];
        var target = new double[8];
        for (var index = 0; index < 8; index++)
        {
            design[index] = [1, index];
            target[index] = 1 + 2 * index;
        }

        var fit = LinearModel.Fit(design, target, ["Свободный член", "Наклон"], [false, false]);
        Assert.NotNull(fit);
        Assert.Equal(1, fit!.RSquared, 6);
        Assert.Equal(1, fit.Terms[0].Coefficient, 5);
        Assert.Equal(2, fit.Terms[1].Coefficient, 5);
        Assert.InRange(LinearModel.TwoTailedNormalP(1.959963984540054), 0.04, 0.06);
    }

    [Fact]
    public void Median_ignores_the_games_without_reviews_toggle()
    {
        var games = new List<GameRecord>
        {
            new() { AppId = 1, Name = "Hit", ReviewsKnown = true, ReviewCount = 100, PositiveRatio = 0.9, ReleaseYear = 2024 },
            new() { AppId = 2, Name = "Quiet", ReviewsKnown = true, ReviewCount = 10, PositiveRatio = 1, ReleaseYear = 2024 },
            new() { AppId = 3, Name = "Silent", ReviewsKnown = true, ReviewCount = 0, ReleaseYear = 2024 }
        };
        var snapshot = DatasetSnapshot.Create(games, [], new Dictionary<int, IReadOnlyList<DailyPoint>>(), false, 10, null);
        var off = new AnalysisWindow { MedianMinReviews = 50, SuccessThreshold = 50, IncludeWithoutReviews = false, MinSegmentSample = 1 };
        var on = off with { IncludeWithoutReviews = true };
        var left = AnalyticsEngine.Overview(snapshot, off, new DateOnly(2026, 10, 5));
        var right = AnalyticsEngine.Overview(snapshot, on, new DateOnly(2026, 10, 5));
        Assert.Equal(100, left.Steam.MedianReviews);
        Assert.Equal(left.Steam.MedianReviews, right.Steam.MedianReviews);
        Assert.Equal(2, left.Steam.Chance!.Total);
        Assert.Equal(10, right.Steam.Chance!.Total);
        Assert.Equal(1, left.Steam.Chance.Successes);
        Assert.Equal(1, right.Steam.Chance.Successes);
    }

    [Fact]
    public void Long_unfinished_list_is_not_marked_empty()
    {
        var walking = SlicePlanner.Assess(5000, 2, 50, 10, true);
        var split = SlicePlanner.Assess(5000, 10, 50, 10, true);
        var deferred = SlicePlanner.Assess(5000, 10, 50, 10, false);
        var empty = SlicePlanner.Assess(0, 1, 50, 10, true);
        Assert.NotEqual(SliceStatus.Empty, walking.Status);
        Assert.Equal(SliceStatus.Split, split.Status);
        Assert.Equal(SliceStatus.Deferred, deferred.Status);
        Assert.Equal(SliceStatus.Empty, empty.Status);
        Assert.Equal(SliceStatus.Complete, SlicePlanner.Assess(5000, 4, 50, 10, true, reachedBeforeTargetYear: true).Status);
    }
}
