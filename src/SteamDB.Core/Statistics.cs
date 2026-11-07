namespace SteamDB.Core;

public readonly record struct WilsonInterval(double Point, double Lower, double Upper)
{
    public static WilsonInterval FromCounts(int successes, int total, double z = 1.959963984540054)
    {
        if (total <= 0)
            return new(0, 0, 0);
        successes = Math.Clamp(successes, 0, total);
        var n = (double)total;
        var phat = successes / n;
        var z2 = z * z;
        var denom = 1 + z2 / n;
        var center = (phat + z2 / (2 * n)) / denom;
        var margin = z * Math.Sqrt((phat * (1 - phat) / n) + z2 / (4 * n * n)) / denom;
        return new(phat, Math.Clamp(center - margin, 0, 1), Math.Clamp(center + margin, 0, 1));
    }
}

public readonly record struct ReachEstimate(double? Typical, double? Low, double? High, bool Calibrated, string Caption);

public readonly record struct UpliftResult(double? Ratio, double? Low, double? High);

public static class Statistics
{
    public static double? Median(IEnumerable<double> values) => Percentile(values, 0.5);

    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Where(static value => !double.IsNaN(value)).OrderBy(static value => value).ToList();
        if (sorted.Count == 0)
            return null;
        if (sorted.Count == 1)
            return sorted[0];
        p = Math.Clamp(p, 0, 1);
        var rank = p * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high)
            return sorted[low];
        var weight = rank - low;
        return sorted[low] * (1 - weight) + sorted[high] * weight;
    }

    public static double? GeometricMean(IEnumerable<double> values)
    {
        var count = 0;
        var sum = 0.0;
        foreach (var value in values)
        {
            if (value <= 0 || double.IsNaN(value))
                continue;
            sum += Math.Log(value);
            count++;
        }

        return count == 0 ? null : Math.Exp(sum / count);
    }

    public static double Smooth(double sample, int sampleSize, double prior, int k)
    {
        var weight = sampleSize + k;
        if (weight <= 0)
            return sample;
        return (sampleSize * sample + k * prior) / weight;
    }

    public static double? PercentileRank(double value, IReadOnlyList<double> population)
    {
        if (population.Count == 0 || double.IsNaN(value))
            return null;
        var below = 0;
        var equal = 0;
        foreach (var item in population)
        {
            if (item < value)
                below++;
            else if (item == value)
                equal++;
        }

        return (below + 0.5 * equal) / population.Count;
    }

    public static double BayesianPositive(int reviews, double ratio, double prior, int m)
    {
        var volume = Math.Max(0, reviews);
        var priorWeight = Math.Max(0, m);
        var denom = volume + priorWeight;
        if (denom <= 0)
            return Math.Clamp(ratio, 0, 1);
        return volume / denom * Math.Clamp(ratio, 0, 1) + priorWeight / denom * Math.Clamp(prior, 0, 1);
    }

    public static double? Trend(int segmentFresh, int allFresh, int segmentPast, int allPast)
    {
        if (allFresh <= 0 || allPast <= 0 || segmentPast <= 0)
            return null;
        var sharePast = segmentPast / (double)allPast;
        if (sharePast <= 0)
            return null;
        var shareFresh = segmentFresh / (double)allFresh;
        return shareFresh / sharePast - 1;
    }

    public static double Opportunity(
        double demandPercentile,
        double successPercentile,
        double competitionPercentile,
        double trendPercentile,
        double weightDemand,
        double weightSuccess,
        double weightCompetition,
        double weightTrend)
    {
        var sum = weightDemand + weightSuccess + weightCompetition + weightTrend;
        if (sum <= 0)
            return 0;
        var score = weightDemand * demandPercentile
            + weightSuccess * successPercentile
            + weightCompetition * (1 - competitionPercentile)
            + weightTrend * trendPercentile;
        return 100 * score / sum;
    }

    public static QuadrantKind Quadrant(double demand, double competition, double medianDemand, double medianCompetition)
    {
        var highDemand = demand >= medianDemand;
        var crowded = competition >= medianCompetition;
        if (highDemand && !crowded)
            return QuadrantKind.BlueOcean;
        if (highDemand && crowded)
            return QuadrantKind.RedOcean;
        if (!highDemand && !crowded)
            return QuadrantKind.QuietNiche;
        return QuadrantKind.Trap;
    }

    public static double? Hot(
        IReadOnlyList<DailyPoint> points,
        DateOnly? releaseDate,
        DateOnly today,
        double onlineWeight,
        double positionWeight,
        double ageExponent)
    {
        var ordered = points.OrderBy(static point => point.Date).ToList();
        if (ordered.Select(static point => point.Date).Distinct().Count() < 2)
            return null;
        var first = ordered[0];
        var last = ordered[^1];
        var days = Math.Max(1, last.Date.DayNumber - first.Date.DayNumber);
        var reviewDelta = (last.ReviewCount ?? first.ReviewCount ?? 0) - (first.ReviewCount ?? 0);
        var onlineDelta = (last.CurrentPlayers ?? 0) - (first.CurrentPlayers ?? 0);
        var position = PositionGrowth(first.Ranks, last.Ranks);
        var ageDays = releaseDate is null ? 365 : Math.Max(0, today.DayNumber - releaseDate.Value.DayNumber);
        var raw = reviewDelta + onlineWeight * onlineDelta + positionWeight * position;
        return raw * 7.0 / days / Math.Pow(ageDays + 2, ageExponent);
    }

    public static ReachEstimate Reach(int? reviews, AnalysisWindow window)
    {
        var calibrated = window.CalibratedUnitsPerReview is > 0;
        if (reviews is null or <= 0)
            return new ReachEstimate(null, null, null, calibrated, calibrated ? "оценка по своим играм" : "оценка");
        if (window.CalibratedUnitsPerReview is double units and > 0)
            return new ReachEstimate(reviews.Value * units, null, null, true, "оценка по своим играм");

        var rate = window.ReviewRate is > 0 and <= 1 ? window.ReviewRate : 0.02;
        var lowRate = window.ReviewRateMin is > 0 and <= 1 ? window.ReviewRateMin : 0.01;
        var highRate = window.ReviewRateMax is > 0 and <= 1 ? window.ReviewRateMax : 0.05;
        if (lowRate > highRate)
            (lowRate, highRate) = (highRate, lowRate);
        return new ReachEstimate(
            reviews.Value / rate,
            reviews.Value / highRate,
            reviews.Value / lowRate,
            false,
            "оценка");
    }

    public static UpliftResult Uplift(int successWith, int countWith, int successWithout, int countWithout)
    {
        if (countWith <= 0 || countWithout <= 0)
            return new UpliftResult(null, null, null);
        var p1 = successWith / (double)countWith;
        var p0 = successWithout / (double)countWithout;
        if (p1 <= 0 || p0 <= 0)
            return new UpliftResult(p0 <= 0 ? null : p1 / p0, null, null);
        var ratio = p1 / p0;
        var se = Math.Sqrt((1 - p1) / (countWith * p1) + (1 - p0) / (countWithout * p0));
        var log = Math.Log(ratio);
        return new UpliftResult(ratio, Math.Exp(log - 1.96 * se), Math.Exp(log + 1.96 * se));
    }

    public static double? PairLift(double? pairMedian, double? medianA, double? medianB)
    {
        if (pairMedian is null || medianA is null || medianB is null)
            return null;
        var baseline = Math.Max(medianA.Value, medianB.Value);
        if (baseline <= 0)
            return null;
        return pairMedian.Value / baseline;
    }

    public static double? Spearman(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 3)
            return null;
        return Pearson(Ranks(left), Ranks(right));
    }

    public static int? ReviewDelta(IReadOnlyList<DailyPoint> points, int minimumDaySpan)
    {
        var ordered = points.Where(static point => point.ReviewCount is not null).OrderBy(static point => point.Date).ToList();
        if (ordered.Count < 2)
            return null;
        var last = ordered[^1];
        var earlier = ordered.LastOrDefault(point => last.Date.DayNumber - point.Date.DayNumber >= minimumDaySpan);
        if (earlier is null)
            return null;
        return last.ReviewCount!.Value - earlier.ReviewCount!.Value;
    }

    public static int? PlayerDelta(IReadOnlyList<DailyPoint> points, int minimumDaySpan)
    {
        var ordered = points.Where(static point => point.CurrentPlayers is not null).OrderBy(static point => point.Date).ToList();
        if (ordered.Count < 2)
            return null;
        var last = ordered[^1];
        var earlier = ordered.LastOrDefault(point => last.Date.DayNumber - point.Date.DayNumber >= minimumDaySpan);
        if (earlier is null)
            return null;
        return last.CurrentPlayers!.Value - earlier.CurrentPlayers!.Value;
    }

    private static double PositionGrowth(IReadOnlyList<RankPoint> older, IReadOnlyList<RankPoint> newer)
    {
        var gains = new List<double>();
        foreach (var next in newer)
        {
            var previous = older.FirstOrDefault(point => point.SegmentId == next.SegmentId);
            if (previous is null || previous.Rank <= 0 || next.Rank <= 0)
                continue;
            gains.Add(previous.Rank - next.Rank);
        }

        return gains.Count == 0 ? 0 : gains.Average();
    }

    private static double[] Ranks(IReadOnlyList<double> values)
    {
        var order = values.Select((value, index) => (value, index)).OrderBy(static item => item.value).ToArray();
        var ranks = new double[values.Count];
        var cursor = 0;
        while (cursor < order.Length)
        {
            var end = cursor;
            while (end + 1 < order.Length && order[end + 1].value == order[cursor].value)
                end++;
            var average = (cursor + end) / 2.0 + 1;
            for (var index = cursor; index <= end; index++)
                ranks[order[index].index] = average;
            cursor = end + 1;
        }

        return ranks;
    }

    private static double? Pearson(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var n = left.Count;
        var meanLeft = left.Average();
        var meanRight = right.Average();
        var num = 0.0;
        var leftSq = 0.0;
        var rightSq = 0.0;
        for (var index = 0; index < n; index++)
        {
            var dx = left[index] - meanLeft;
            var dy = right[index] - meanRight;
            num += dx * dy;
            leftSq += dx * dx;
            rightSq += dy * dy;
        }

        var denom = Math.Sqrt(leftSq * rightSq);
        return denom == 0 ? null : num / denom;
    }
}
