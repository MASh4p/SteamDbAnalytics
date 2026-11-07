namespace SteamDB.Core;

public static class AnalyticsEngine
{
    public static IReadOnlyList<GameRecord> Visible(DatasetSnapshot snapshot, AnalysisWindow window) =>
        window.HideAdult ? snapshot.Games.Where(static game => !game.IsAdult).ToList() : snapshot.Games.ToList();

    public static OverviewReport Overview(DatasetSnapshot snapshot, AnalysisWindow window, DateOnly today)
    {
        var games = Visible(snapshot, window);
        var steam = Baseline(games, snapshot.StoreGameCount, window);
        var stored = Score(BuildStored(snapshot, games, window, today, steam), window);
        var niches = stored
            .Where(item => item.Metrics.Kind is SegmentKind.Tag or SegmentKind.Genre && !item.Metrics.SmallSample)
            .OrderByDescending(item => item.Metrics.Chance?.Lower ?? -1)
            .Take(8)
            .ToList();
        var hottest = HotList(snapshot, games, window, today);
        return new OverviewReport(
            steam,
            Buckets(games),
            Cohorts(games, window),
            niches,
            games.Where(static game => game.ReviewsKnown && game.ReviewCount is > 0)
                .OrderByDescending(static game => game.ReviewCount)
                .Take(8)
                .ToList(),
            hottest,
            snapshot.HasTwoSnapshotDays,
            !snapshot.DeepCollectionComplete,
            Conclusions(snapshot, steam, niches, hottest.Count > 0));
    }

    public static IReadOnlyList<ScoredSegment> Segments(DatasetSnapshot snapshot, AnalysisWindow window, DateOnly today, SegmentKind? kind)
    {
        var games = Visible(snapshot, window);
        var steam = Baseline(games, snapshot.StoreGameCount, window);
        var metrics = kind is null
            ? BuildStored(snapshot, games, window, today, steam).Concat(BuildVirtual(games, window, today, steam))
            : kind is SegmentKind.Tag or SegmentKind.Genre
                ? BuildStored(snapshot, games, window, today, steam).Where(item => item.Kind == kind)
                : BuildVirtual(games, window, today, steam).Where(item => item.Kind == kind);
        return Score(metrics.ToList(), window);
    }

    public static IReadOnlyList<FormatRow> Formats(DatasetSnapshot snapshot, AnalysisWindow window, DateOnly today)
    {
        var games = Visible(snapshot, window);
        var steam = Baseline(games, snapshot.StoreGameCount, window);
        var rows = BuildVirtual(games, window, today, steam);
        return rows.Select(row => new FormatRow(
            GroupOf(row),
            row.Name,
            row,
            steam.Chance is null || steam.Chance.Point <= 0 || row.Chance is null ? null : row.Chance.Point / steam.Chance.Point)).ToList();
    }

    public static IReadOnlyList<GameRecord> QueryGames(DatasetSnapshot snapshot, AnalysisWindow window, GameQuery query, DateOnly today)
    {
        var games = Visible(snapshot, window).Where(game => MatchesQuery(game, query)).ToList();
        return SortGames(snapshot, games, window, query.Sort, today);
    }

    public static GameInsight? Inspect(DatasetSnapshot snapshot, AnalysisWindow window, int appId, DateOnly today)
    {
        if (!snapshot.ByAppId.TryGetValue(appId, out var game))
            return null;
        if (window.HideAdult && game.IsAdult)
            return null;
        var games = Visible(snapshot, window);
        var history = snapshot.History.TryGetValue(appId, out var points) ? points : [];
        var prior = AveragePositive(games);
        var quality = game.ReviewsKnown && game.PositiveRatio is not null && game.ReviewCount is not null
            ? Statistics.BayesianPositive(game.ReviewCount.Value, game.PositiveRatio.Value, prior, window.BayesianM)
            : (double?)null;
        var ranked = games.Where(static item => item.ReviewsKnown && item.ReviewCount is not null).Select(static item => (double)item.ReviewCount!.Value).ToList();
        double? better = game.ReviewCount is int reviews && ranked.Count > 0
            ? ranked.Count(value => value < reviews) / (double)ranked.Count
            : null;
        var windowForReach = window with { CalibratedUnitsPerReview = snapshot.CalibratedUnitsPerReview ?? window.CalibratedUnitsPerReview };
        return new GameInsight(
            game,
            quality,
            better,
            Statistics.Hot(history, game.ReleaseDate, today, window.HotOnlineWeight, window.HotPositionWeight, window.HotAgeExponent),
            Statistics.ReviewDelta(history, 1),
            Statistics.Reach(game.ReviewCount, windowForReach),
            history.OrderBy(static point => point.Date).ToList(),
            Positions(snapshot, game, history),
            Similar(games, game));
    }

    public static IReadOnlyList<PairReport> Pairs(DatasetSnapshot snapshot, AnalysisWindow window, int minimumGames, string? search, int take = 200)
    {
        var games = Visible(snapshot, window);
        var names = snapshot.Segments
            .Where(static segment => segment.SteamTagId is not null)
            .GroupBy(static segment => segment.SteamTagId!.Value)
            .ToDictionary(static group => group.Key, static group => group.First().Name);
        var eligible = new Dictionary<int, int>();
        foreach (var game in games)
        {
            foreach (var tag in game.TagIds.Distinct())
            {
                eligible.TryGetValue(tag, out var count);
                eligible[tag] = count + 1;
            }
        }

        var candidates = eligible
            .Where(item => item.Value >= minimumGames && names.ContainsKey(item.Key))
            .OrderByDescending(item => item.Value)
            .Take(50)
            .Select(item => item.Key)
            .ToHashSet();
        var single = candidates.ToDictionary(static id => id, _ => new PairAccumulator());
        var pairs = new Dictionary<(int A, int B), PairAccumulator>();
        foreach (var game in games)
        {
            var tags = game.TagIds.Where(candidates.Contains).Distinct().OrderBy(static id => id).ToArray();
            foreach (var tag in tags)
                single[tag].Add(game, window);
            for (var left = 0; left < tags.Length; left++)
            {
                for (var right = left + 1; right < tags.Length; right++)
                {
                    var key = (tags[left], tags[right]);
                    if (!pairs.TryGetValue(key, out var acc))
                        pairs[key] = acc = new PairAccumulator();
                    acc.Add(game, window);
                }
            }
        }

        var reports = new List<PairReport>();
        foreach (var (key, acc) in pairs)
        {
            if (acc.Collected < minimumGames)
                continue;
            var nameA = names[key.A];
            var nameB = names[key.B];
            if (!string.IsNullOrWhiteSpace(search)
                && !nameA.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !nameB.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            var medianA = Statistics.Median(single[key.A].MedianValues);
            var medianB = Statistics.Median(single[key.B].MedianValues);
            var medianPair = Statistics.Median(acc.MedianValues);
            var denom = window.IncludeWithoutReviews ? acc.Collected : acc.WithReviews;
            reports.Add(new PairReport(
                key.A,
                nameA,
                key.B,
                nameB,
                acc.Collected,
                medianPair,
                medianA,
                medianB,
                Statistics.PairLift(medianPair, medianA, medianB),
                ProportionView.Create(acc.Successes, denom),
                acc.Collected < window.MinSegmentSample));
        }

        return reports
            .OrderByDescending(item => item.Lift ?? -1)
            .ThenByDescending(item => item.Count)
            .Take(take)
            .ToList();
    }

    public static IdeaReport Idea(DatasetSnapshot snapshot, AnalysisWindow window, IdeaRequest request, DateOnly today)
    {
        var games = Visible(snapshot, window);
        var matched = games.Where(game => MatchesIdea(game, request)).ToList();
        var medianValues = MedianValues(matched, window);
        var median = Statistics.Median(medianValues);
        var p75 = Statistics.Percentile(medianValues, 0.75);
        var p90 = Statistics.Percentile(medianValues, 0.90);
        var thresholds = new[] { 100, 500, 5000 };
        var chances = thresholds.Select(threshold =>
        {
            var local = Chance(matched, threshold, CountWithReviews(matched), window.IncludeWithoutReviews ? EstimateStore(snapshot, request, matched.Count) ?? matched.Count : null);
            var steam = Chance(games, threshold, CountWithReviews(games), window.IncludeWithoutReviews ? snapshot.StoreGameCount ?? games.Count : null);
            return (threshold, local, steam);
        }).ToList();
        var reachWindow = window with { CalibratedUnitsPerReview = snapshot.CalibratedUnitsPerReview ?? window.CalibratedUnitsPerReview };
        var (add, avoid) = Advice(matched, request, window, snapshot);
        return new IdeaReport(
            matched.Count,
            EstimateStore(snapshot, request, matched.Count) ?? matched.Count,
            median,
            p75,
            p90,
            chances,
            Statistics.Reach(median is null ? null : (int)Math.Round(median.Value), reachWindow),
            Statistics.Reach(p90 is null ? null : (int)Math.Round(p90.Value), reachWindow),
            add,
            avoid);
    }

    public static FactorReport Factors(DatasetSnapshot snapshot, AnalysisWindow window, DateOnly today)
    {
        var games = Visible(snapshot, window).Where(static game => game.CardRead && game.ReviewsKnown).ToList();
        var rows = new List<FactorRow>();
        var correlations = new List<SpearmanRow>();
        var definitions = FeatureDefinitions();
        foreach (var feature in definitions)
        {
            var with = games.Where(feature.Matches).ToList();
            var without = games.Where(game => !feature.Matches(game)).ToList();
            var uplift = Statistics.Uplift(
                CountSuccess(with, window.SuccessThreshold),
                with.Count,
                CountSuccess(without, window.SuccessThreshold),
                without.Count);
            rows.Add(new FactorRow(
                feature.Name,
                feature.Group,
                with.Count,
                without.Count,
                Statistics.Median(MedianValues(with, window)),
                Statistics.Median(MedianValues(without, window)),
                Chance(with, window.SuccessThreshold, CountWithReviews(with), null),
                Chance(without, window.SuccessThreshold, CountWithReviews(without), null),
                uplift.Ratio,
                uplift.Low,
                uplift.High,
                null,
                null,
                null,
                false,
                null));
            var x = games.Select(game => feature.Matches(game) ? 1.0 : 0.0).ToList();
            var y = games.Select(game => Math.Log(1 + Math.Max(0, game.ReviewCount ?? 0))).ToList();
            correlations.Add(new SpearmanRow(feature.Name, Statistics.Spearman(x, y)));
        }

        var ages = games.Where(static game => game.ReleaseDate is not null).Select(game => (double)Math.Max(0, today.DayNumber - game.ReleaseDate!.Value.DayNumber)).ToList();
        var agedGames = games.Where(static game => game.ReleaseDate is not null).ToList();
        correlations.Add(new SpearmanRow("Возраст, дни", Statistics.Spearman(ages, agedGames.Select(game => Math.Log(1 + Math.Max(0, game.ReviewCount ?? 0))).ToList())));
        correlations.Add(new SpearmanRow("Скриншоты", Statistics.Spearman(games.Select(static game => (double)game.ScreenshotCount).ToList(), games.Select(static game => Math.Log(1 + Math.Max(0, game.ReviewCount ?? 0))).ToList())));

        double? r2 = null;
        double? ageDoubling = null;
        double? shotDoubling = null;
        var ready = agedGames.Count >= LinearModel.MinimumSample;
        if (ready)
        {
            var usable = definitions.Where(feature =>
            {
                var yes = agedGames.Count(feature.Matches);
                return yes >= 15 && agedGames.Count - yes >= 15;
            }).ToList();
            var names = new List<string> { "Свободный член", "Возраст", "Скриншоты" };
            names.AddRange(usable.Select(static feature => feature.Name));
            var doubling = names.Select(name => name is "Возраст" or "Скриншоты").ToList();
            var design = new double[agedGames.Count][];
            var target = new double[agedGames.Count];
            for (var row = 0; row < agedGames.Count; row++)
            {
                var game = agedGames[row];
                var age = Math.Max(0, today.DayNumber - game.ReleaseDate!.Value.DayNumber);
                var line = new double[names.Count];
                line[0] = 1;
                line[1] = Math.Log(1 + age);
                line[2] = Math.Log(1 + game.ScreenshotCount);
                for (var col = 0; col < usable.Count; col++)
                    line[col + 3] = usable[col].Matches(game) ? 1 : 0;
                design[row] = line;
                target[row] = Math.Log(1 + Math.Max(0, game.ReviewCount ?? 0));
            }

            var fit = LinearModel.Fit(design, target, names, doubling);
            if (fit is not null)
            {
                r2 = fit.RSquared;
                ageDoubling = fit.Terms.FirstOrDefault(term => term.Name == "Возраст")?.Multiplier;
                shotDoubling = fit.Terms.FirstOrDefault(term => term.Name == "Скриншоты")?.Multiplier;
                rows = rows.Select(row =>
                {
                    var term = fit.Terms.FirstOrDefault(item => item.Name == row.Name);
                    return term is null
                        ? row
                        : row with
                        {
                            Multiplier = term.Multiplier,
                            MultiplierLow = term.MultiplierLow,
                            MultiplierHigh = term.MultiplierHigh,
                            Starred = term.Starred,
                            PValue = term.PValue
                        };
                }).ToList();
            }
            else
            {
                ready = false;
            }
        }

        return new FactorReport(games.Count, ready, r2, ageDoubling, shotDoubling, rows, correlations);
    }

    public static CreatorSummary Creators(DatasetSnapshot snapshot, AnalysisWindow window, bool publishers)
    {
        var games = Visible(snapshot, window);
        var groups = new Dictionary<string, List<GameRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var names = publishers ? game.Publishers : game.Developers;
            foreach (var name in names.Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!groups.TryGetValue(name, out var list))
                    groups[name] = list = [];
                list.Add(game);
            }
        }

        var rows = groups.Select(pair =>
        {
            var reviews = pair.Value.Where(static game => game.ReviewsKnown && game.ReviewCount is not null).Select(static game => game.ReviewCount!.Value).ToList();
            var best = pair.Value.OrderByDescending(static game => game.ReviewCount ?? -1).First();
            int? reviewGrowth = null;
            int? playerGrowth = null;
            var hasSpan = false;
            foreach (var game in pair.Value)
            {
                if (!snapshot.History.TryGetValue(game.AppId, out var history))
                    continue;
                var reviewsDelta = Statistics.ReviewDelta(history, 30);
                var playersDelta = Statistics.PlayerDelta(history, 30);
                if (reviewsDelta is not null || playersDelta is not null)
                    hasSpan = true;
                if (reviewsDelta is int reviewValue)
                    reviewGrowth = (reviewGrowth ?? 0) + reviewValue;
                if (playersDelta is int playerValue)
                    playerGrowth = (playerGrowth ?? 0) + playerValue;
            }

            snapshot.CreatorFollowers.TryGetValue(pair.Key, out var followers);
            return new CreatorReport(
                pair.Key,
                followers,
                pair.Value.Count,
                reviews.Sum(),
                Statistics.Median(reviews.Select(static value => (double)value)),
                best.Name,
                best.AppId,
                hasSpan ? reviewGrowth : null,
                hasSpan ? playerGrowth : null);
        }).OrderByDescending(static row => row.TotalReviews).ToList();

        var totalReviews = rows.Sum(static row => row.TotalReviews);
        var topCount = Math.Max(1, (int)Math.Ceiling(rows.Count * 0.01));
        var topShare = totalReviews <= 0 ? (double?)null : rows.Take(topCount).Sum(static row => row.TotalReviews) / (double)totalReviews;
        var bands = new (string Label, int Min, int Max)[]
        {
            ("1 игра", 1, 1),
            ("2–3 игры", 2, 3),
            ("4–9 игр", 4, 9),
            ("10+ игр", 10, int.MaxValue)
        };
        var experience = bands.Select(band =>
        {
            var authors = rows.Where(row => row.GameCount >= band.Min && row.GameCount <= band.Max).ToList();
            var medians = authors.SelectMany(author => gamesFor(author.Name)).Where(static game => game.ReviewsKnown && game.ReviewCount is not null).Select(static game => (double)game.ReviewCount!.Value);
            return new ExperienceBand(band.Label, authors.Count, Statistics.Median(medians));
        }).ToList();

        return new CreatorSummary(
            rows.Count,
            rows.Count(static row => row.GameCount == 1),
            topShare,
            Statistics.Median(rows.Select(static row => (double)row.TotalReviews)),
            experience,
            rows);

        IEnumerable<GameRecord> gamesFor(string name) => groups[name];
    }

    public static IReadOnlyList<string> ReadingNotes { get; } =
    [
        "Медиана считается только по играм с отзывами не ниже порога шапки. Переключатель игр без отзывов на неё не влияет.",
        "Шанс — доля игр с отзывами не ниже порога успеха и нижняя граница 95% интервала Уилсона. В рейтинг ниш идёт нижняя граница.",
        "Конкуренция — total_count магазина, если включены игры без отзывов, иначе число игр с хотя бы одним отзывом.",
        "Тренд сравнивает доли успешных игр, поэтому общий рост Steam его не раздувает.",
        "Opportunity собирает перцентили спроса, успеха, низкой конкуренции и тренда. Веса меняются в сборе данных.",
        "Квадрант сравнивает спрос и число игр с медианами сегментов: голубой океан, красный океан, тихая ниша или ловушка."
    ];

    private static SteamBaseline Baseline(IReadOnlyList<GameRecord> games, int? storeCount, AnalysisWindow window)
    {
        var known = games.Where(static game => game.ReviewsKnown).ToList();
        var zeros = known.Count(static game => (game.ReviewCount ?? 0) == 0);
        var medianValues = MedianValues(games, window);
        var withReviews = CountWithReviews(games);
        return new SteamBaseline(
            games.Count,
            storeCount,
            known.Count == 0 ? null : zeros / (double)known.Count,
            Statistics.Median(medianValues),
            Statistics.Percentile(medianValues, 0.90),
            Statistics.GeometricMean(games.Where(static game => game.ReviewsKnown && game.ReviewCount is > 0).Select(static game => (double)game.ReviewCount!.Value)),
            Chance(games, window.SuccessThreshold, withReviews, window.IncludeWithoutReviews ? storeCount ?? games.Count : null),
            AveragePositive(games));
    }

    private static List<SegmentMetrics> BuildStored(
        DatasetSnapshot snapshot,
        IReadOnlyList<GameRecord> games,
        AnalysisWindow window,
        DateOnly today,
        SteamBaseline steam)
    {
        var byTag = IndexTags(games);
        var byGenre = IndexGenres(games);
        var list = new List<SegmentMetrics>();
        foreach (var segment in snapshot.Segments)
        {
            List<GameRecord> members;
            if (segment.Kind == SegmentKind.Genre)
            {
                byGenre.TryGetValue(segment.Name, out var genreGames);
                members = genreGames ?? [];
            }
            else if (segment.SteamTagId is int tagId && byTag.TryGetValue(tagId, out var tagged))
            {
                members = tagged;
            }
            else
            {
                members = [];
            }

            list.Add(Measure(segment.Slug, segment.Name, segment.Kind, segment.SteamTagId, segment.TotalCount, members, games, window, today, steam, segment));
        }

        return list;
    }

    private static List<SegmentMetrics> BuildVirtual(IReadOnlyList<GameRecord> games, AnalysisWindow window, DateOnly today, SteamBaseline steam)
    {
        var specs = new List<(string Key, string Name, SegmentKind Kind, string Group, Func<GameRecord, bool> Match)>();
        void Add(string key, string name, SegmentKind kind, Func<GameRecord, bool> match) =>
            specs.Add((key, name, kind, "", match));

        Add("platform:windows", "Windows", SegmentKind.Platform, static game => game.Windows);
        Add("platform:mac", "macOS", SegmentKind.Platform, static game => game.Mac);
        Add("platform:linux", "Linux", SegmentKind.Platform, static game => game.Linux);
        Add("deck:verified", "Steam Deck: проверена", SegmentKind.Platform, static game => game.Deck == DeckStatus.Verified);
        Add("deck:playable", "Steam Deck: играбельна", SegmentKind.Platform, static game => game.Deck == DeckStatus.Playable);
        Add("deck:unsupported", "Steam Deck: не поддерживается", SegmentKind.Platform, static game => game.Deck == DeckStatus.Unsupported);
        Add("cat:controller", "Контроллер", SegmentKind.Category, static game => game.Controller);
        Add("cat:vr", "VR", SegmentKind.Category, static game => game.Vr);
        Add("cat:single", "Одиночная", SegmentKind.Category, static game => game.SinglePlayer);
        Add("cat:coop", "Кооператив", SegmentKind.Category, static game => game.Coop);
        Add("cat:multi", "Мультиплеер", SegmentKind.Category, static game => game.Multiplayer);
        Add("fmt:demo", "Демо", SegmentKind.Format, static game => game.HasDemo);
        Add("fmt:early", "Ранний доступ", SegmentKind.Format, static game => game.Status == ReleaseStatus.EarlyAccess);
        Add("fmt:cloud", "Облако", SegmentKind.Format, static game => game.Cloud);
        Add("fmt:workshop", "Мастерская", SegmentKind.Format, static game => game.Workshop);
        Add("fmt:achievements", "Достижения", SegmentKind.Format, static game => game.Achievements);
        Add("fmt:trailer", "Есть трейлер", SegmentKind.Format, static game => game.HasTrailer);
        foreach (var shelf in Taxonomy.Shelves)
            Add("price:" + shelf.Key, shelf.Name, SegmentKind.Format, game => Taxonomy.MatchesShelf(game, shelf.Key));
        Add("lang:1", "1 язык", SegmentKind.Language, static game => game.LanguageCount == 1);
        Add("lang:2-4", "2–4 языка", SegmentKind.Language, static game => game.LanguageCount is >= 2 and <= 4);
        Add("lang:5-9", "5–9 языков", SegmentKind.Language, static game => game.LanguageCount is >= 5 and <= 9);
        Add("lang:10", "10+ языков", SegmentKind.Language, static game => game.LanguageCount is >= 10);

        return specs.Select(spec =>
        {
            var members = games.Where(spec.Match).ToList();
            return Measure(spec.Key, spec.Name, spec.Kind, null, null, members, games, window, today, steam, null);
        }).ToList();
    }

    private static SegmentMetrics Measure(
        string key,
        string name,
        SegmentKind kind,
        int? steamTagId,
        int? storeCount,
        IReadOnlyList<GameRecord> members,
        IReadOnlyList<GameRecord> all,
        AnalysisWindow window,
        DateOnly today,
        SteamBaseline steam,
        SegmentRecord? source)
    {
        var medianValues = MedianValues(members, window);
        var median = Statistics.Median(medianValues);
        var withReviews = CountWithReviews(members);
        var geo = Statistics.GeometricMean(members.Where(static game => game.ReviewsKnown && game.ReviewCount is > 0).Select(static game => (double)game.ReviewCount!.Value));
        double? demand = geo is null || steam.GeometricMean is null
            ? geo
            : Statistics.Smooth(geo.Value, members.Count(static game => game.ReviewsKnown && game.ReviewCount is > 0), steam.GeometricMean.Value, window.SmoothingK);
        var smoothed = median is null || steam.MedianReviews is null
            ? median
            : Statistics.Smooth(median.Value, medianValues.Count, steam.MedianReviews.Value, window.SmoothingK);
        var competition = window.IncludeWithoutReviews ? (double?)(storeCount ?? members.Count) : withReviews;
        var freshYears = new[] { today.Year - 1, today.Year };
        var pastYears = new[] { today.Year - 3, today.Year - 2 };
        var trend = Statistics.Trend(
            CountSuccessfulYears(members, freshYears, window.SuccessThreshold),
            CountSuccessfulYears(all, freshYears, window.SuccessThreshold),
            CountSuccessfulYears(members, pastYears, window.SuccessThreshold),
            CountSuccessfulYears(all, pastYears, window.SuccessThreshold));
        double? entry = null;
        int? growth = null;
        if (source is not null)
        {
            var popular = source.PopularAppIds
                .Select(id => members.FirstOrDefault(game => game.AppId == id))
                .Where(static game => game is not null && game.ReviewsKnown && game.ReviewCount is not null)
                .Select(static game => (double)game!.ReviewCount!.Value)
                .ToList();
            entry = Statistics.Median(popular);
            if (source.SizeHistory.Count >= 2)
            {
                var ordered = source.SizeHistory.OrderBy(static point => point.Date).ToList();
                growth = ordered[^1].TotalCount - ordered[0].TotalCount;
            }
        }

        return new SegmentMetrics
        {
            Key = key,
            Name = name,
            Kind = kind,
            SteamTagId = steamTagId,
            StoreCount = storeCount,
            Collected = members.Count,
            WithReviews = withReviews,
            MedianSample = medianValues.Count,
            MedianReviews = median,
            SmoothedMedian = smoothed,
            P25 = Statistics.Percentile(medianValues, 0.25),
            P75 = Statistics.Percentile(medianValues, 0.75),
            P90 = Statistics.Percentile(medianValues, 0.90),
            Chance = Chance(members, window.SuccessThreshold, withReviews, window.IncludeWithoutReviews ? storeCount ?? members.Count : null),
            Trend = trend,
            EntryThreshold = entry,
            Demand = demand,
            CompetitionSize = competition,
            SmallSample = withReviews < window.MinSegmentSample,
            SizeGrowth = growth
        };
    }

    public static IReadOnlyList<ScoredSegment> Score(IReadOnlyList<SegmentMetrics> metrics, AnalysisWindow window)
    {
        var reference = metrics.Where(static item => !item.SmallSample && item.Demand is not null).ToList();
        if (reference.Count == 0)
            reference = metrics.Where(static item => item.Demand is not null).ToList();
        var demands = reference.Select(static item => item.Demand ?? 0).ToList();
        var successes = reference.Select(static item => item.Chance?.Lower ?? 0).ToList();
        var competitions = reference.Select(static item => Math.Log(1 + Math.Max(0, item.CompetitionSize ?? 0))).ToList();
        var trends = reference.Where(static item => item.Trend is not null).Select(static item => item.Trend!.Value).ToList();
        var medianDemand = Statistics.Median(demands) ?? 0;
        var medianCompetition = Statistics.Median(reference.Select(static item => item.CompetitionSize ?? 0)) ?? 0;
        return metrics.Select(item =>
        {
            var demandP = Statistics.PercentileRank(item.Demand ?? 0, demands) ?? 0.5;
            var successP = Statistics.PercentileRank(item.Chance?.Lower ?? 0, successes) ?? 0.5;
            var competitionP = Statistics.PercentileRank(Math.Log(1 + Math.Max(0, item.CompetitionSize ?? 0)), competitions) ?? 0.5;
            var trendP = item.Trend is null || trends.Count == 0
                ? 0.5
                : Statistics.PercentileRank(item.Trend.Value, trends) ?? 0.5;
            return new ScoredSegment(
                item,
                demandP,
                successP,
                competitionP,
                trendP,
                Statistics.Opportunity(demandP, successP, competitionP, trendP, window.WeightDemand, window.WeightSuccess, window.WeightCompetition, window.WeightTrend),
                Statistics.Quadrant(item.Demand ?? 0, item.CompetitionSize ?? 0, medianDemand, medianCompetition));
        }).OrderByDescending(static item => item.Opportunity).ToList();
    }

    private static ProportionView? Chance(IReadOnlyList<GameRecord> games, int threshold, int reviewedCount, int? storeCount)
    {
        var successes = CountSuccess(games, threshold);
        var denom = storeCount is int store ? Math.Max(store, successes) : reviewedCount;
        return ProportionView.Create(successes, denom);
    }

    private static int CountSuccess(IEnumerable<GameRecord> games, int threshold) =>
        games.Count(game => game.ReviewsKnown && game.ReviewCount >= threshold);

    private static int CountWithReviews(IEnumerable<GameRecord> games) =>
        games.Count(static game => game.ReviewsKnown && game.ReviewCount is > 0);

    private static int CountSuccessfulYears(IEnumerable<GameRecord> games, IReadOnlyCollection<int> years, int threshold) =>
        games.Count(game => game.ReleaseYear is int year && years.Contains(year) && game.ReviewsKnown && game.ReviewCount >= threshold);

    private static List<double> MedianValues(IEnumerable<GameRecord> games, AnalysisWindow window) =>
        games.Where(game => game.ReviewsKnown && game.ReviewCount >= window.MedianMinReviews)
            .Select(static game => (double)game.ReviewCount!.Value)
            .ToList();

    private static double AveragePositive(IEnumerable<GameRecord> games)
    {
        var ratios = games.Where(static game => game.ReviewsKnown && game.ReviewCount is > 0 && game.PositiveRatio is not null)
            .Select(static game => game.PositiveRatio!.Value)
            .ToList();
        return ratios.Count == 0 ? 0.8 : ratios.Average();
    }

    private static Dictionary<int, List<GameRecord>> IndexTags(IReadOnlyList<GameRecord> games)
    {
        var index = new Dictionary<int, List<GameRecord>>();
        foreach (var game in games)
        {
            foreach (var tag in game.TagIds.Distinct())
            {
                if (!index.TryGetValue(tag, out var list))
                    index[tag] = list = [];
                list.Add(game);
            }
        }

        return index;
    }

    private static Dictionary<string, List<GameRecord>> IndexGenres(IReadOnlyList<GameRecord> games)
    {
        var index = new Dictionary<string, List<GameRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            foreach (var genre in game.Genres.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!index.TryGetValue(genre, out var list))
                    index[genre] = list = [];
                list.Add(game);
            }
        }

        return index;
    }

    private static List<BucketCount> Buckets(IReadOnlyList<GameRecord> games)
    {
        var known = games.Where(static game => game.ReviewsKnown && game.ReviewCount is not null).Select(static game => game.ReviewCount!.Value).ToList();
        (string Label, int Min, int Max)[] bands =
        [
            ("0", 0, 0),
            ("1–9", 1, 9),
            ("10–49", 10, 49),
            ("50–99", 50, 99),
            ("100–499", 100, 499),
            ("500–4999", 500, 4999),
            ("5000+", 5000, int.MaxValue)
        ];
        return bands.Select(band => new BucketCount(band.Label, known.Count(value => value >= band.Min && value <= band.Max))).ToList();
    }

    private static List<YearCohort> Cohorts(IReadOnlyList<GameRecord> games, AnalysisWindow window) =>
        games.Where(static game => game.ReleaseYear is not null)
            .GroupBy(static game => game.ReleaseYear!.Value)
            .OrderBy(static group => group.Key)
            .Select(group => new YearCohort(group.Key, group.Count(), Statistics.Median(MedianValues(group, window))))
            .ToList();

    private static List<HotGame> HotList(DatasetSnapshot snapshot, IReadOnlyList<GameRecord> games, AnalysisWindow window, DateOnly today)
    {
        if (!snapshot.HasTwoSnapshotDays)
            return [];
        return games.Select(game =>
        {
            snapshot.History.TryGetValue(game.AppId, out var history);
            var score = Statistics.Hot(history ?? [], game.ReleaseDate, today, window.HotOnlineWeight, window.HotPositionWeight, window.HotAgeExponent);
            return new HotGame(game, score);
        }).Where(static item => item.Score is not null)
            .OrderByDescending(static item => item.Score)
            .Take(8)
            .ToList();
    }

    private static List<string> Conclusions(DatasetSnapshot snapshot, SteamBaseline steam, IReadOnlyList<ScoredSegment> niches, bool hasHot)
    {
        var lines = new List<string>();
        if (snapshot.Games.Count == 0)
        {
            lines.Add("База пустая. Кнопка в шапке запустит быстрый старт: первые выводы появятся по заметным играм, и доли успеха будут завышены.");
            return lines;
        }

        lines.Add(steam.MedianReviews is null
            ? "Медиану отзывов пока не из чего считать: мало игр выше порога шапки."
            : $"Медиана отзывов среди игр с порогом шапки — {steam.MedianReviews.Value:0}. P90 — {(steam.P90 ?? 0):0}.");
        if (steam.Chance is not null)
            lines.Add($"Шанс набрать порог успеха: {steam.Chance.Point:P1}, нижняя граница Уилсона {steam.Chance.Lower:P1}.");
        if (!snapshot.DeepCollectionComplete)
            lines.Add(Labels.Bias);
        if (niches.Count > 0)
            lines.Add($"Выше по нижней границе шанса сейчас «{niches[0].Metrics.Name}».");
        lines.Add(hasHot ? "По снимкам уже можно смотреть горячее." : Labels.HotEmpty);
        return lines;
    }

    private static bool MatchesQuery(GameRecord game, GameQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Text) && !game.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.Genre) && !game.Genres.Contains(query.Genre, StringComparer.OrdinalIgnoreCase))
            return false;
        if (query.TagId is int tag && !game.TagIds.Contains(tag))
            return false;
        if (query.Platform == "windows" && !game.Windows)
            return false;
        if (query.Platform == "mac" && !game.Mac)
            return false;
        if (query.Platform == "linux" && !game.Linux)
            return false;
        if (!Taxonomy.MatchesShelf(game, query.PriceShelfKey))
            return false;
        if (query.Year is int year && game.ReleaseYear != year)
            return false;
        if (query.Deck is DeckStatus deck && deck != DeckStatus.Unknown && game.Deck != deck)
            return false;
        if (query.EarlyAccessOnly && game.Status != ReleaseStatus.EarlyAccess)
            return false;
        return true;
    }

    private static List<GameRecord> SortGames(DatasetSnapshot snapshot, List<GameRecord> games, AnalysisWindow window, GameSort sort, DateOnly today)
    {
        double Key(GameRecord game) => sort switch
        {
            GameSort.ReviewQuality => game.PositiveRatio is null || game.ReviewCount is null
                ? double.MinValue
                : Statistics.BayesianPositive(game.ReviewCount.Value, game.PositiveRatio.Value, AveragePositive(games), window.BayesianM),
            GameSort.Hot => Statistics.Hot(HistoryOf(snapshot, game.AppId), game.ReleaseDate, today, window.HotOnlineWeight, window.HotPositionWeight, window.HotAgeExponent) ?? double.MinValue,
            GameSort.ReviewGrowth => Statistics.ReviewDelta(HistoryOf(snapshot, game.AppId), 1) ?? double.MinValue,
            GameSort.Newest => game.ReleaseDate?.DayNumber ?? int.MinValue,
            GameSort.Online => game.CurrentPlayers ?? int.MinValue,
            _ => game.ReviewCount ?? int.MinValue
        };

        return games.OrderByDescending(Key).ThenBy(static game => game.Name).ToList();
    }

    private static IReadOnlyList<DailyPoint> HistoryOf(DatasetSnapshot snapshot, int appId) =>
        snapshot.History.TryGetValue(appId, out var history) ? history : [];

    private static List<TagPosition> Positions(DatasetSnapshot snapshot, GameRecord game, IReadOnlyList<DailyPoint> history)
    {
        var latest = history.OrderBy(static point => point.Date).LastOrDefault();
        if (latest is null || latest.Ranks.Count == 0)
            return [];
        var result = new List<TagPosition>();
        foreach (var rank in latest.Ranks.OrderBy(static item => item.Rank))
        {
            var segment = snapshot.Segments.FirstOrDefault(item => item.Id == rank.SegmentId);
            var neighbors = new List<GameRecord>();
            foreach (var (appId, points) in snapshot.History)
            {
                if (appId == game.AppId)
                    continue;
                var point = points.Where(item => item.Date == latest.Date).OrderByDescending(static item => item.Date).FirstOrDefault();
                var near = point?.Ranks.FirstOrDefault(item => item.SegmentId == rank.SegmentId && Math.Abs(item.Rank - rank.Rank) <= 2 && item.Rank != rank.Rank);
                if (near is null || !snapshot.ByAppId.TryGetValue(appId, out var neighbor))
                    continue;
                neighbors.Add(neighbor);
            }

            result.Add(new TagPosition(segment?.Name ?? $"Тег {rank.SegmentId}", rank.Rank, neighbors.Take(4).ToList()));
        }

        return result;
    }

    private static List<GameRecord> Similar(IReadOnlyList<GameRecord> games, GameRecord game)
    {
        if (game.TagIds.Count == 0)
            return [];
        var mine = game.TagIds.ToHashSet();
        return games.Where(item => item.AppId != game.AppId && item.TagIds.Count > 0)
            .Select(item =>
            {
                var shared = item.TagIds.Count(mine.Contains);
                var union = mine.Count + item.TagIds.Distinct().Count() - shared;
                return (item, score: union == 0 ? 0 : shared / (double)union);
            })
            .Where(static item => item.score > 0)
            .OrderByDescending(static item => item.score)
            .ThenByDescending(static item => item.item.ReviewCount ?? 0)
            .Take(8)
            .Select(static item => item.item)
            .ToList();
    }

    private static bool MatchesIdea(GameRecord game, IdeaRequest request)
    {
        if (request.TagIds.Count > 0 && !request.TagIds.All(game.TagIds.Contains))
            return false;
        if (!string.IsNullOrWhiteSpace(request.Genre) && !game.Genres.Contains(request.Genre, StringComparer.OrdinalIgnoreCase))
            return false;
        if (request.Platform == "windows" && !game.Windows)
            return false;
        if (request.Platform == "mac" && !game.Mac)
            return false;
        if (request.Platform == "linux" && !game.Linux)
            return false;
        if (!Taxonomy.MatchesShelf(game, request.PriceShelfKey))
            return false;
        if (request.Deck is DeckStatus deck && deck != DeckStatus.Unknown && game.Deck != deck)
            return false;
        if (request.Multiplayer == true && !game.Multiplayer)
            return false;
        if (request.Multiplayer == false && game.Multiplayer)
            return false;
        return true;
    }

    private static int? EstimateStore(DatasetSnapshot snapshot, IdeaRequest request, int matched)
    {
        if (request.TagIds.Count == 1 && string.IsNullOrWhiteSpace(request.Genre) && string.IsNullOrWhiteSpace(request.Platform)
            && string.IsNullOrWhiteSpace(request.PriceShelfKey) && request.Deck is null or DeckStatus.Unknown && request.Multiplayer is null)
        {
            var segment = snapshot.Segments.FirstOrDefault(item => item.SteamTagId == request.TagIds[0]);
            if (segment?.TotalCount is int total)
                return total;
        }

        return matched;
    }

    private static (List<TagAdvice> Add, List<TagAdvice> Avoid) Advice(IReadOnlyList<GameRecord> matched, IdeaRequest request, AnalysisWindow window, DatasetSnapshot snapshot)
    {
        var names = snapshot.Segments.Where(static segment => segment.SteamTagId is not null)
            .GroupBy(static segment => segment.SteamTagId!.Value)
            .ToDictionary(static group => group.Key, static group => group.First().Name);
        var chosen = request.TagIds.ToHashSet();
        var add = new List<TagAdvice>();
        var avoid = new List<TagAdvice>();
        var tags = matched.SelectMany(static game => game.TagIds).Where(tag => !chosen.Contains(tag) && names.ContainsKey(tag)).Distinct().ToList();
        foreach (var tag in tags)
        {
            var with = matched.Where(game => game.TagIds.Contains(tag)).ToList();
            var without = matched.Where(game => !game.TagIds.Contains(tag)).ToList();
            if (with.Count < request.NeighborMin || without.Count < request.NeighborMin)
                continue;
            var medianWith = Statistics.Median(MedianValues(with, window));
            var medianWithout = Statistics.Median(MedianValues(without, window));
            if (medianWith is null || medianWithout is null || medianWithout <= 0)
                continue;
            var advice = new TagAdvice(names[tag], tag, medianWith, medianWithout, with.Count, without.Count);
            var ratio = medianWith.Value / medianWithout.Value;
            if (ratio >= 1.15)
                add.Add(advice);
            else if (ratio <= 0.85)
                avoid.Add(advice);
        }

        return (
            add.OrderByDescending(static item => item.MedianWith / item.MedianWithout).Take(6).ToList(),
            avoid.OrderBy(static item => item.MedianWith / item.MedianWithout).Take(6).ToList());
    }

    private static string GroupOf(SegmentMetrics metrics) => metrics.Key.Split(':')[0] switch
    {
        "platform" => "Платформы",
        "deck" => "Steam Deck",
        "cat" => metrics.Name is "Контроллер" or "VR" ? "Управление и VR" : "Режимы",
        "price" => "Цена",
        "lang" => "Языки",
        _ => "Страница"
    };

    private static IReadOnlyList<FeatureDef> FeatureDefinitions() =>
    [
        new("Ранний доступ", "Статус", static game => game.Status == ReleaseStatus.EarlyAccess),
        new("Демо", "Страница", static game => game.HasDemo),
        new("Бесплатная", "Цена", static game => game.IsFree),
        new("macOS", "Платформа", static game => game.Mac),
        new("Linux", "Платформа", static game => game.Linux),
        new("Steam Deck: проверена", "Платформа", static game => game.Deck == DeckStatus.Verified),
        new("Контроллер", "Управление", static game => game.Controller),
        new("VR", "Формат", static game => game.Vr),
        new("Кооп", "Режим", static game => game.Coop),
        new("Мультиплеер", "Режим", static game => game.Multiplayer),
        new("Только одиночная", "Режим", static game => game.SinglePlayer && !game.Coop && !game.Multiplayer),
        new("Достижения", "Страница", static game => game.Achievements),
        new("Облако", "Страница", static game => game.Cloud),
        new("Мастерская", "Страница", static game => game.Workshop),
        new("Трейлер", "Страница", static game => game.HasTrailer),
        new("5+ языков", "Языки", static game => game.LanguageCount >= 5),
        new("Цена ≤ $9.99", "Цена", static game => !game.IsFree && game.PriceCents is int price && price <= 999),
        new("Цена ≥ $29.99", "Цена", static game => game.PriceCents is >= 2999)
    ];

    private sealed class PairAccumulator
    {
        public int Collected { get; private set; }
        public int WithReviews { get; private set; }
        public int Successes { get; private set; }
        public List<double> MedianValues { get; } = [];

        public void Add(GameRecord game, AnalysisWindow window)
        {
            Collected++;
            if (game.ReviewsKnown && game.ReviewCount is > 0)
                WithReviews++;
            if (game.ReviewsKnown && game.ReviewCount >= window.SuccessThreshold)
                Successes++;
            if (game.ReviewsKnown && game.ReviewCount >= window.MedianMinReviews)
                MedianValues.Add(game.ReviewCount!.Value);
        }
    }

    private sealed record FeatureDef(string Name, string Group, Func<GameRecord, bool> Matches);
}
