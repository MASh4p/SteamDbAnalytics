using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamDB.Core;
using SteamDB.Infrastructure.Data;
using SteamDB.Infrastructure.Steam;

namespace SteamDB.Infrastructure;

public sealed class CollectionCoordinator(
    IServiceScopeFactory scopes,
    ISettingsStore settings,
    IDatasetCache cache,
    PoliteSteamClient client,
    ILogger<CollectionCoordinator> logger) : BackgroundService, ICollectionController
{
    private readonly Channel<CollectionProfile> _channel = Channel.CreateUnbounded<CollectionProfile>();
    private int _stop;
    private CollectionStatus _status = CollectionStatus.Idle;

    public CollectionStatus Status => _status;
    public event Action? Changed;

    public ValueTask EnqueueAsync(CollectionProfile profile)
    {
        Interlocked.Exchange(ref _stop, 0);
        client.Resume();
        return _channel.Writer.WriteAsync(profile);
    }

    public void RequestStop() => Interlocked.Exchange(ref _stop, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await settings.LoadAsync(stoppingToken);
            await using (var scope = scopes.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var stale = await db.Runs.Where(run => run.Status == "running").ToListAsync(stoppingToken);
                foreach (var run in stale)
                {
                    run.Status = "Stopped";
                    run.FinishedAt = DateTimeOffset.Now;
                    run.Message = "Приложение закрылось во время сбора. Прогресс в базе сохранён.";
                }

                if (stale.Count > 0)
                    await db.SaveChangesAsync(stoppingToken);
            }

            await cache.ReloadAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось открыть локальную базу");
            Publish(new CollectionStatus { State = RunState.Failed, Title = "База не открылась", LastError = ex.Message });
        }

        var dailyTick = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (_channel.Reader.TryRead(out var profile))
                    await RunAsync(profile, stoppingToken);
                if (DateTimeOffset.UtcNow - dailyTick > TimeSpan.FromMinutes(1))
                {
                    dailyTick = DateTimeOffset.UtcNow;
                    await MaybeDailyAsync(stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Сбой цикла сбора");
                Publish(new CollectionStatus { State = RunState.Failed, Title = "Сбор остановился с ошибкой", LastError = ex.Message });
            }
        }
    }

    private async Task MaybeDailyAsync(CancellationToken ct)
    {
        var current = settings.Current;
        if (_status.State == RunState.Running)
            return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var context = await db.CreateDbContextAsync(ct);
        var any = await context.Games.AnyAsync(ct);
        if (!any)
            return;
        var due = current.LastDailyRunUtc is null || DateTimeOffset.UtcNow - current.LastDailyRunUtc > TimeSpan.FromHours(24);
        if (due)
            await EnqueueAsync(CollectionProfile.Daily);
    }

    private async Task RunAsync(CollectionProfile profile, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<CollectionRunner>();
        try
        {
            await runner.ExecuteAsync(profile, Publish, () => Volatile.Read(ref _stop) == 1, ct);
        }
        finally
        {
            await cache.ReloadAsync(ct);
        }
    }

    private void Publish(CollectionStatus status)
    {
        _status = status;
        Changed?.Invoke();
    }
}

public sealed class CollectionRunner(
    IDbContextFactory<AppDbContext> factory,
    ISettingsStore settings,
    PoliteSteamClient client,
    IDatasetCache cache,
    ILogger<CollectionRunner> logger)
{
    public async Task ExecuteAsync(CollectionProfile profile, Action<CollectionStatus> publish, Func<bool> stop, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var requests = 0;
        string? message = null;
        var state = RunState.Completed;
        await using var db = await factory.CreateDbContextAsync(ct);
        if (profile == CollectionProfile.Daily && !await db.Games.AnyAsync(ct))
            profile = CollectionProfile.QuickStart;

        var run = new RunEntity { Profile = profile.ToString(), StartedAt = started, Status = "running" };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        Publish(publish, profile, "Идёт " + Labels.Profile(profile), null, 0, null, started);

        try
        {
            message = profile switch
            {
                CollectionProfile.QuickStart => await QuickAsync(db, publish, stop, started, () => ++requests, ct),
                CollectionProfile.Daily => await DailyAsync(db, publish, stop, started, () => ++requests, ct),
                CollectionProfile.Deep => await DeepAsync(db, publish, stop, started, () => ++requests, ct),
                CollectionProfile.CardBackfill => await BackfillAsync(db, publish, stop, started, () => ++requests, ct),
                _ => "Неизвестный профиль"
            } ?? "Готово";
            if (stop() || client.IsPaused)
                state = client.IsPaused ? RunState.Failed : RunState.Stopped;
            if (profile is CollectionProfile.Daily or CollectionProfile.QuickStart && state == RunState.Completed)
                await settings.UpdateAsync(item => item.LastDailyRunUtc = DateTimeOffset.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state = RunState.Stopped;
            message = "Приложение закрывается, прогресс сохранён.";
        }
        catch (Exception ex)
        {
            state = RunState.Failed;
            message = ex.Message;
            logger.LogError(ex, "Ошибка сбора");
            db.Errors.Add(new ErrorEntity { At = DateTimeOffset.Now, Kind = "сбой", Message = Trim(ex.Message) });
        }

        run.FinishedAt = DateTimeOffset.Now;
        run.Status = state.ToString();
        run.Message = message;
        run.Requests = requests;
        await db.SaveChangesAsync(ct);
        await cache.ReloadAsync(CancellationToken.None);
        Publish(publish, profile, state switch
        {
            RunState.Failed => "Сбор остановлен",
            RunState.Stopped => "Сбор остановлен вручную",
            _ => "Порция собрана"
        }, message, requests, requests, started, client.PauseReason ?? message);
    }

    private async Task<string> QuickAsync(AppDbContext db, Action<CollectionStatus> publish, Func<bool> stop, DateTimeOffset started, Func<int> tick, CancellationToken ct)
    {
        var options = settings.Current;
        Publish(publish, CollectionProfile.QuickStart, "Справочник тегов", "Публичный список популярных тегов", 0, null, started);
        if (!await FetchTagsAsync(db, tick, ct))
            return client.PauseReason ?? "Справочник тегов не получен.";
        if (stop())
            return "Остановлено.";

        var tags = await db.Segments.Where(item => item.Kind == (int)SegmentKind.Tag && item.SteamTagId != null)
            .OrderBy(item => item.Id).Take(options.QuickTagCount).ToListAsync(ct);
        var jobs = tags.Select(tag => new StoreQuery { TagIds = [tag.SteamTagId!.Value] }).ToList();
        foreach (var genre in Taxonomy.MajorGenres)
            jobs.Add(new StoreQuery { TagIds = [genre.Id] });
        jobs.Add(new StoreQuery { Filter = "globaltopsellers" });
        jobs.Add(new StoreQuery());
        var index = 0;
        foreach (var job in jobs)
        {
            if (stop() || client.IsPaused)
                break;
            index++;
            Publish(publish, CollectionProfile.QuickStart, "Поиск магазина", $"Список {index} из {jobs.Count}", index, jobs.Count, started);
            var page = await SearchAsync(db, job, 0, options.SearchPageSize, rememberPopular: job.TagIds.Count == 1, tick, ct);
            if (page is null)
                break;
            if (job.TagIds.Count == 0 && job.Filter is null)
                await settings.UpdateAsync(item => item.StoreGameCount = page.TotalCount, ct);
        }

        var recent = new StoreQuery { Sort = "Released_DESC" };
        for (var pageIndex = 0; pageIndex < Math.Max(1, options.QuickPagesPerList); pageIndex++)
        {
            if (stop() || client.IsPaused)
                break;
            var page = await SearchAsync(db, recent, pageIndex * options.SearchPageSize, options.SearchPageSize, false, tick, ct);
            if (page is null || page.Hits.Count == 0)
                break;
            var years = page.Hits.Select(hit => SteamParsers.ParseRelease(hit.ReleaseText).Year).Where(year => year is not null).Select(year => year!.Value).ToList();
            if (years.Count > 0 && years.Max() < DateTime.Now.Year - 1)
                break;
        }

        await cache.ReloadAsync(ct);
        await HydrateAsync(db, options.QuickDetailBudget, publish, stop, started, tick, CollectionProfile.QuickStart, ct);
        return client.IsPaused
            ? client.PauseReason!
            : "Быстрый старт смотрит заметные игры. Доли успеха пока приблизительные и завышены.";
    }

    private async Task<string> DailyAsync(AppDbContext db, Action<CollectionStatus> publish, Func<bool> stop, DateTimeOffset started, Func<int> tick, CancellationToken ct)
    {
        var options = settings.Current;
        var tags = await db.Segments.Where(item => item.Kind == (int)SegmentKind.Tag && item.SteamTagId != null)
            .OrderByDescending(item => item.TotalCount).Take(options.QuickTagCount).ToListAsync(ct);
        var step = 0;
        foreach (var tag in tags)
        {
            if (stop() || client.IsPaused)
                break;
            step++;
            Publish(publish, CollectionProfile.Daily, "Топы тегов", tag.Name, step, tags.Count, started);
            await SearchAsync(db, new StoreQuery { TagIds = [tag.SteamTagId!.Value] }, 0, options.SearchPageSize, true, tick, ct);
        }

        if (!stop() && !client.IsPaused)
            await SearchAsync(db, new StoreQuery { Sort = "Released_DESC" }, 0, options.SearchPageSize, false, tick, ct);

        var tracked = await db.Tracked.Select(item => item.AppId).ToListAsync(ct);
        var collected = await db.Games.OrderByDescending(item => item.ReviewCount).Select(item => item.AppId).Take(options.DailyRefreshBudget).ToListAsync(ct);
        var ids = tracked.Concat(collected).Distinct().Take(options.DailyRefreshBudget).ToList();
        var done = 0;
        foreach (var appId in ids)
        {
            if (stop() || client.IsPaused)
                break;
            done++;
            Publish(publish, CollectionProfile.Daily, "Отзывы и онлайн", $"Игра {appId}", done, ids.Count, started);
            var entity = await db.Games.FirstOrDefaultAsync(item => item.AppId == appId, ct);
            if (entity is null)
                continue;
            if (!entity.CardRead)
                await DetailsAsync(db, entity, tick, ct);
            await ReviewsAsync(db, entity, tick, ct);
            await PlayersAsync(db, entity, tick, ct);
            if (done % 20 == 0)
                await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return client.PauseReason ?? "Ежедневное обновление записало снимок.";
    }

    private async Task<string> DeepAsync(AppDbContext db, Action<CollectionStatus> publish, Func<bool> stop, DateTimeOffset started, Func<int> tick, CancellationToken ct)
    {
        var options = settings.Current;
        await EnsureSlicesAsync(db, options, ct);
        var pages = 0;
        while (pages < options.DeepSearchPagesPerRun && !stop() && !client.IsPaused)
        {
            var slice = await db.Slices.Where(item => item.Status == (int)SliceStatus.Pending || item.Status == (int)SliceStatus.Partial)
                .OrderBy(item => item.Id).FirstOrDefaultAsync(ct);
            if (slice is null)
                break;
            pages++;
            Publish(publish, CollectionProfile.Deep, "Глубокий сбор", slice.Key, pages, options.DeepSearchPagesPerRun, started);
            var query = new StoreQuery
            {
                TagIds = slice.TagId is int tag ? [tag] : [],
                MaxPriceCents = slice.MaxPriceCents,
                Os = slice.Os,
                Category2 = slice.Category2,
                Sort = slice.Sort
            };
            var page = await SearchAsync(db, query, slice.PagesFetched * options.SearchPageSize, options.SearchPageSize, slice.PagesFetched == 0 && slice.TagId is not null && !slice.PriceUsed && !slice.OsUsed && !slice.CategoryUsed, tick, ct);
            if (page is null)
                break;
            slice.PagesFetched++;
            slice.TotalCount = page.TotalCount;
            var years = page.Hits.Select(hit => SteamParsers.ParseRelease(hit.ReleaseText).Year).Where(year => year is not null).Select(year => year!.Value).ToList();
            var reached = slice.YearWalk && slice.Year is int target && years.Count > 0 && years.Min() < target;
            var canSplit = !slice.PriceUsed || string.IsNullOrEmpty(slice.Os) || slice.Category2 is null;
            var assessment = SlicePlanner.Assess(page.TotalCount, slice.PagesFetched, options.SearchPageSize, options.MaxPagesBeforeSplit, canSplit, reached);
            slice.Status = (int)assessment.Status;
            slice.Note = assessment.Reason;
            if (assessment.Status == SliceStatus.Split)
                AddChildren(db, slice);
            await db.SaveChangesAsync(ct);
        }

        await HydrateAsync(db, options.DeepDetailBudgetPerRun, publish, stop, started, tick, CollectionProfile.Deep, ct);
        var open = await db.Slices.AnyAsync(item => item.Status == (int)SliceStatus.Pending || item.Status == (int)SliceStatus.Partial || item.Status == (int)SliceStatus.Deferred, ct);
        var anySlice = await db.Slices.AnyAsync(ct);
        await settings.UpdateAsync(item => item.DeepCollectionComplete = !open && anySlice, ct);
        return client.PauseReason ?? (open
            ? "Порция глубокого сбора сохранена. Длинные списки разрезаны или отложены, пустыми они не считаются."
            : "Глубокий сбор дошёл до конца очереди.");
    }

    private async Task<string> BackfillAsync(AppDbContext db, Action<CollectionStatus> publish, Func<bool> stop, DateTimeOffset started, Func<int> tick, CancellationToken ct)
    {
        var options = settings.Current;
        var ids = await db.Games.Where(item => !item.CardRead || item.TagIdsJson == "[]" || item.TagIdsJson == "null")
            .OrderByDescending(item => item.ReviewCount)
            .Select(item => item.AppId)
            .Take(options.QuickDetailBudget)
            .ToListAsync(ct);
        var names = await db.Segments.Where(item => item.SteamTagId != null).ToDictionaryAsync(item => item.Name, item => item.SteamTagId!.Value, StringComparer.OrdinalIgnoreCase, ct);
        var done = 0;
        foreach (var appId in ids)
        {
            if (stop() || client.IsPaused)
                break;
            done++;
            Publish(publish, CollectionProfile.CardBackfill, "Дочитка карточек", $"Игра {appId}", done, ids.Count, started);
            var entity = await db.Games.FirstAsync(item => item.AppId == appId, ct);
            if (!entity.CardRead && !entity.PublicCardUnavailable)
                await DetailsAsync(db, entity, tick, ct);
            if ((JsonWork.Read<List<int>>(entity.TagIdsJson) ?? []).Count == 0 && !entity.PublicCardUnavailable)
                await TagsFromPageAsync(db, entity, names, tick, ct);
            if (done % 10 == 0)
                await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return client.PauseReason ?? "Дочитка карточек без тегов и состава страницы сохранена.";
    }

    private async Task HydrateAsync(AppDbContext db, int budget, Action<CollectionStatus> publish, Func<bool> stop, DateTimeOffset started, Func<int> tick, CollectionProfile profile, CancellationToken ct)
    {
        var ids = await db.Games.Where(item => !item.CardRead || !item.ReviewsKnown)
            .OrderByDescending(item => item.ReviewCount)
            .Select(item => item.AppId)
            .Take(budget)
            .ToListAsync(ct);
        var done = 0;
        foreach (var appId in ids)
        {
            if (stop() || client.IsPaused)
                break;
            done++;
            Publish(publish, profile, "Карточки, отзывы, онлайн", $"Игра {appId}", done, ids.Count, started);
            var entity = await db.Games.FirstAsync(item => item.AppId == appId, ct);
            if (!entity.CardRead)
                await DetailsAsync(db, entity, tick, ct);
            if (!entity.ReviewsKnown)
                await ReviewsAsync(db, entity, tick, ct);
            await PlayersAsync(db, entity, tick, ct);
            if (done % 10 == 0)
            {
                await db.SaveChangesAsync(ct);
                await cache.ReloadAsync(ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> FetchTagsAsync(AppDbContext db, Func<int> tick, CancellationToken ct)
    {
        var fetch = await client.GetAsync(SteamUrls.PopularTags(), ct);
        tick();
        if (!fetch.Ok)
        {
            await Fail(db, "сеть", fetch.StatusCode, fetch.Message, ct);
            return !fetch.Stop;
        }

        var tags = SteamParsers.Tags(fetch.Body);
        if (tags.Count == 0)
        {
            await Fail(db, "разбор", fetch.StatusCode, "Справочник тегов пуст или не разобран.", ct);
            return true;
        }

        foreach (var tag in tags)
            await UpsertTagAsync(db, tag.Id, tag.Name, null, null, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<SearchPage?> SearchAsync(AppDbContext db, StoreQuery query, int start, int count, bool rememberPopular, Func<int> tick, CancellationToken ct)
    {
        var fetch = await client.GetAsync(SteamUrls.Search(query, start, count), ct);
        tick();
        if (!fetch.Ok)
        {
            await Fail(db, "сеть", fetch.StatusCode, fetch.Message, ct);
            return null;
        }

        var page = SteamParsers.Search(fetch.Body);
        if (page is null)
        {
            await Fail(db, "разбор", fetch.StatusCode, "Выдача поиска не разобрана.", ct);
            return null;
        }

        SegmentEntity? segment = null;
        if (query.TagIds.Count == 1)
            segment = await UpsertTagAsync(db, query.TagIds[0], "Тег " + query.TagIds[0], page.TotalCount, rememberPopular ? page.Hits.Select(hit => hit.AppId).ToList() : null, ct);
        var rank = start;
        foreach (var hit in page.Hits)
        {
            rank++;
            var entity = await UpsertHitAsync(db, hit, ct);
            if (segment is not null && rememberPopular)
                await TouchSnapshotAsync(db, entity, segment.Id == 0 ? null : segment.Id, rank, ct);
        }

        await db.SaveChangesAsync(ct);
        if (segment is { Id: > 0 } && rememberPopular)
        {
            rank = start;
            foreach (var hit in page.Hits)
            {
                rank++;
                var entity = await db.Games.FirstAsync(item => item.AppId == hit.AppId, ct);
                await TouchSnapshotAsync(db, entity, segment.Id, rank, ct);
            }

            await db.SaveChangesAsync(ct);
        }

        return page;
    }

    private async Task DetailsAsync(AppDbContext db, GameEntity entity, Func<int> tick, CancellationToken ct)
    {
        var fetch = await client.GetAsync(SteamUrls.AppDetails(entity.AppId), ct);
        tick();
        if (!fetch.Ok)
        {
            await Fail(db, "сеть", fetch.StatusCode, fetch.Message, ct);
            return;
        }

        var parsed = SteamParsers.AppDetails(fetch.Body, entity.AppId);
        if (parsed is null)
        {
            await Fail(db, "разбор", fetch.StatusCode, $"Карточка {entity.AppId} не разобрана.", ct);
            return;
        }

        if (parsed.Unavailable)
        {
            entity.PublicCardUnavailable = true;
            entity.UpdatedAt = DateTimeOffset.Now;
            return;
        }

        ApplyCard(entity, parsed);
        foreach (var genre in parsed.Genres)
            await UpsertGenreAsync(db, genre, ct);
    }

    private async Task ReviewsAsync(AppDbContext db, GameEntity entity, Func<int> tick, CancellationToken ct)
    {
        var all = await client.GetAsync(SteamUrls.Reviews(entity.AppId, "all"), ct);
        tick();
        if (!all.Ok)
        {
            await Fail(db, "сеть", all.StatusCode, all.Message, ct);
            return;
        }

        var summary = SteamParsers.Reviews(all.Body);
        if (summary is null)
        {
            await Fail(db, "разбор", all.StatusCode, $"Отзывы {entity.AppId} не разобраны.", ct);
            return;
        }

        entity.ReviewCount = summary.Total;
        entity.PositiveReviews = summary.Positive;
        entity.PositiveRatio = summary.Ratio;
        entity.ReviewsKnown = true;
        var recent = await client.GetAsync(SteamUrls.Reviews(entity.AppId, "recent"), ct);
        tick();
        if (recent.Ok)
        {
            var recentSummary = SteamParsers.Reviews(recent.Body);
            entity.RecentPositiveRatio = recentSummary?.Ratio;
        }

        entity.UpdatedAt = DateTimeOffset.Now;
        await TouchSnapshotAsync(db, entity, null, null, ct);
    }

    private async Task PlayersAsync(AppDbContext db, GameEntity entity, Func<int> tick, CancellationToken ct)
    {
        var fetch = await client.GetAsync(SteamUrls.Players(entity.AppId), ct);
        tick();
        if (!fetch.Ok)
        {
            await Fail(db, "сеть", fetch.StatusCode, fetch.Message, ct);
            return;
        }

        var players = SteamParsers.CurrentPlayers(fetch.Body);
        if (players is null)
            return;
        entity.CurrentPlayers = players;
        entity.PeakPlayers = Math.Max(entity.PeakPlayers ?? 0, players.Value);
        entity.UpdatedAt = DateTimeOffset.Now;
        await TouchSnapshotAsync(db, entity, null, null, ct);
    }

    private async Task TagsFromPageAsync(AppDbContext db, GameEntity entity, Dictionary<string, int> names, Func<int> tick, CancellationToken ct)
    {
        var fetch = await client.GetAsync(SteamUrls.AppPage(entity.AppId), ct);
        tick();
        if (!fetch.Ok)
        {
            await Fail(db, "сеть", fetch.StatusCode, fetch.Message, ct);
            return;
        }

        if (SteamParsers.LooksLikeAgeGate(fetch.Body))
        {
            entity.PublicCardUnavailable = true;
            return;
        }

        var tags = SteamParsers.StorePageTags(fetch.Body);
        var ids = JsonWork.Read<List<int>>(entity.TagIdsJson) ?? [];
        foreach (var name in tags)
        {
            if (names.TryGetValue(name, out var id) && !ids.Contains(id))
                ids.Add(id);
        }

        entity.TagIdsJson = JsonWork.Of(ids);
        entity.UpdatedAt = DateTimeOffset.Now;
    }

    private static async Task<GameEntity> UpsertHitAsync(AppDbContext db, SearchHit hit, CancellationToken ct)
    {
        var entity = await db.Games.FirstOrDefaultAsync(item => item.AppId == hit.AppId, ct);
        if (entity is null)
        {
            entity = new GameEntity { AppId = hit.AppId, Name = hit.Name, Url = hit.Url };
            db.Games.Add(entity);
        }

        if (!entity.CardRead && !string.IsNullOrWhiteSpace(hit.Name))
            entity.Name = hit.Name;
        if (!string.IsNullOrWhiteSpace(hit.Url))
            entity.Url = hit.Url;
        var tags = JsonWork.Read<List<int>>(entity.TagIdsJson) ?? [];
        foreach (var tag in hit.TagIds.Where(tag => !tags.Contains(tag)))
            tags.Add(tag);
        entity.TagIdsJson = JsonWork.Of(tags);
        if (hit.PriceCents is int price)
        {
            entity.PriceCents = price;
            entity.Currency = "USD";
        }

        entity.IsFree = hit.IsFree || entity.IsFree;
        entity.Windows = entity.Windows || hit.Windows;
        entity.Mac = entity.Mac || hit.Mac;
        entity.Linux = entity.Linux || hit.Linux;
        var release = SteamParsers.ParseRelease(hit.ReleaseText);
        entity.ReleaseDate ??= release.Date;
        entity.ReleaseYear ??= release.Year;
        if (hit.ReviewCount is int reviews)
        {
            entity.ReviewCount = reviews;
            entity.ReviewsKnown = true;
            if (hit.PositiveRatio is double ratio)
            {
                entity.PositiveRatio = ratio;
                entity.PositiveReviews = (int)Math.Round(reviews * ratio);
            }
        }

        entity.UpdatedAt = DateTimeOffset.Now;
        return entity;
    }

    private static void ApplyCard(GameEntity entity, ParsedGame parsed)
    {
        entity.Name = string.IsNullOrWhiteSpace(parsed.Name) ? entity.Name : parsed.Name;
        entity.Url = parsed.Url ?? entity.Url;
        entity.HeaderImage = parsed.HeaderImage;
        entity.ShortDescription = parsed.ShortDescription;
        entity.DevelopersJson = JsonWork.Of(parsed.Developers);
        entity.PublishersJson = JsonWork.Of(parsed.Publishers);
        entity.ReleaseDate = parsed.ReleaseDate ?? entity.ReleaseDate;
        entity.ReleaseYear = parsed.ReleaseYear ?? entity.ReleaseYear;
        entity.PriceCents = parsed.PriceCents ?? entity.PriceCents;
        entity.Currency = parsed.Currency ?? entity.Currency;
        entity.DiscountPercent = parsed.DiscountPercent;
        entity.IsFree = parsed.IsFree;
        entity.Windows = parsed.Windows;
        entity.Mac = parsed.Mac;
        entity.Linux = parsed.Linux;
        entity.GenresJson = JsonWork.Of(parsed.Genres);
        entity.SinglePlayer = parsed.SinglePlayer;
        entity.Coop = parsed.Coop;
        entity.Multiplayer = parsed.Multiplayer;
        entity.Vr = parsed.Vr;
        entity.Controller = parsed.Controller;
        entity.Achievements = parsed.Achievements;
        entity.Cloud = parsed.Cloud;
        entity.Workshop = parsed.Workshop;
        entity.HasDemo = parsed.HasDemo;
        entity.Status = (int)(parsed.ComingSoon ? ReleaseStatus.Announced : parsed.EarlyAccess ? ReleaseStatus.EarlyAccess : ReleaseStatus.Released);
        entity.Deck = parsed.DeckCategory;
        entity.LanguageCount = parsed.LanguageCount;
        entity.ScreenshotCount = parsed.ScreenshotCount;
        entity.HasTrailer = parsed.HasTrailer;
        entity.IsAdult = parsed.IsAdult;
        entity.CardRead = true;
        entity.PublicCardUnavailable = false;
        entity.UpdatedAt = DateTimeOffset.Now;
    }

    private static async Task<SegmentEntity> UpsertTagAsync(AppDbContext db, int tagId, string name, int? total, IReadOnlyList<int>? popular, CancellationToken ct)
    {
        var entity = await db.Segments.FirstOrDefaultAsync(item => item.Kind == (int)SegmentKind.Tag && item.SteamTagId == tagId, ct);
        if (entity is null)
        {
            entity = new SegmentEntity
            {
                Slug = "tag-" + tagId,
                Name = name,
                Kind = (int)SegmentKind.Tag,
                SteamTagId = tagId
            };
            db.Segments.Add(entity);
        }

        if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("Тег ", StringComparison.Ordinal))
            entity.Name = name;
        if (total is int count)
        {
            entity.TotalCount = count;
            var history = JsonWork.Read<List<SegmentSizePoint>>(entity.HistoryJson) ?? [];
            var today = DateOnly.FromDateTime(DateTime.Now);
            history.RemoveAll(item => item.Date == today);
            history.Add(new SegmentSizePoint(today, count));
            entity.HistoryJson = JsonWork.Of(history);
        }

        if (popular is not null)
            entity.PopularJson = JsonWork.Of(popular);
        return entity;
    }

    private static async Task UpsertGenreAsync(AppDbContext db, string name, CancellationToken ct)
    {
        var slug = "genre-" + Taxonomy.Slug(name);
        if (await db.Segments.AnyAsync(item => item.Kind == (int)SegmentKind.Genre && item.Slug == slug, ct))
            return;
        db.Segments.Add(new SegmentEntity { Slug = slug, Name = name, Kind = (int)SegmentKind.Genre });
    }

    private static async Task TouchSnapshotAsync(AppDbContext db, GameEntity game, int? segmentId, int? rank, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var snapshot = await db.Snapshots.FirstOrDefaultAsync(item => item.AppId == game.AppId && item.Date == today, ct);
        if (snapshot is null)
        {
            snapshot = new SnapshotEntity { AppId = game.AppId, Date = today };
            db.Snapshots.Add(snapshot);
        }

        snapshot.ReviewCount = game.ReviewCount;
        snapshot.PositiveRatio = game.PositiveRatio;
        snapshot.CurrentPlayers = game.CurrentPlayers;
        if (segmentId is int segment && rank is int position)
        {
            var ranks = JsonWork.Read<List<RankPoint>>(snapshot.RanksJson) ?? [];
            ranks.RemoveAll(item => item.SegmentId == segment);
            ranks.Add(new RankPoint(segment, position));
            snapshot.RanksJson = JsonWork.Of(ranks);
        }
    }

    private static async Task EnsureSlicesAsync(AppDbContext db, AppSettings options, CancellationToken ct)
    {
        if (await db.Slices.AnyAsync(ct))
            return;
        var tags = await db.Segments.Where(item => item.SteamTagId != null).ToListAsync(ct);
        var majors = Taxonomy.MajorGenres.Select(item => item.Id).ToHashSet();
        foreach (var tag in tags)
        {
            if (tag.TotalCount is int total && total < options.DeepMinSegmentSize && tag.SteamTagId is int id && !majors.Contains(id))
                continue;
            AddSlice(db, new SliceEntity { TagId = tag.SteamTagId, Sort = "_ASC" });
        }

        for (var year = options.DeepStartYear; year <= DateTime.Now.Year; year++)
            AddSlice(db, new SliceEntity { Year = year, YearWalk = true, Sort = "Released_DESC" });
        await db.SaveChangesAsync(ct);
    }

    private static void AddChildren(AppDbContext db, SliceEntity parent)
    {
        if (!parent.PriceUsed)
        {
            foreach (var cap in new[] { 499, 999, 1499, 1999, 2999 })
            {
                AddSlice(db, Clone(parent, slice =>
                {
                    slice.MaxPriceCents = cap;
                    slice.PriceUsed = true;
                }));
            }

            AddSlice(db, Clone(parent, slice =>
            {
                slice.PriceUsed = true;
                slice.Status = (int)SliceStatus.Deferred;
                slice.Note = "Хвост дороже $29.99 отложен: у поиска нет нижней цены, список не помечен пустым.";
            }));
            return;
        }

        if (string.IsNullOrEmpty(parent.Os))
        {
            foreach (var os in new[] { "win", "mac", "linux" })
            {
                AddSlice(db, Clone(parent, slice =>
                {
                    slice.Os = os;
                    slice.OsUsed = true;
                }));
            }

            return;
        }

        if (parent.Category2 is null)
        {
            foreach (var category in new[] { Taxonomy.SinglePlayerCategory, Taxonomy.MultiplayerCategory, Taxonomy.CoopCategory, Taxonomy.ControllerCategory, Taxonomy.AchievementsCategory })
            {
                AddSlice(db, Clone(parent, slice =>
                {
                    slice.Category2 = category;
                    slice.CategoryUsed = true;
                }));
            }

            return;
        }

        parent.Status = (int)SliceStatus.Deferred;
        parent.Note = "Дальше фильтр не дробится. Список отложен и не помечен пустым.";
    }

    private static SliceEntity Clone(SliceEntity parent, Action<SliceEntity> change)
    {
        var child = new SliceEntity
        {
            TagId = parent.TagId,
            Year = parent.Year,
            MaxPriceCents = parent.MaxPriceCents,
            Os = parent.Os,
            Category2 = parent.Category2,
            Sort = parent.Sort,
            YearWalk = parent.YearWalk,
            PriceUsed = parent.PriceUsed,
            OsUsed = parent.OsUsed,
            CategoryUsed = parent.CategoryUsed,
            Status = (int)SliceStatus.Pending
        };
        change(child);
        return child;
    }

    private static void AddSlice(AppDbContext db, SliceEntity slice)
    {
        slice.Key = $"t{slice.TagId}|y{slice.Year}|p{slice.MaxPriceCents}|o{slice.Os}|c{slice.Category2}|s{slice.Sort}|w{(slice.YearWalk ? 1 : 0)}|d{(slice.Status == (int)SliceStatus.Deferred ? "x" : "")}";
        if (db.Slices.Local.Any(item => item.Key == slice.Key) || db.Slices.Any(item => item.Key == slice.Key))
            return;
        db.Slices.Add(slice);
    }

    private static async Task Fail(AppDbContext db, string kind, int code, string message, CancellationToken ct)
    {
        db.Errors.Add(new ErrorEntity { At = DateTimeOffset.Now, Kind = kind, StatusCode = code == 0 ? null : code, Message = Trim(message) });
        await db.SaveChangesAsync(ct);
    }

    private static void Publish(Action<CollectionStatus> publish, CollectionProfile profile, string title, string? detail, int done, int? planned, DateTimeOffset started, string? error = null) =>
        publish(new CollectionStatus
        {
            State = RunState.Running,
            Profile = profile,
            Title = title,
            Detail = detail,
            Done = done,
            Planned = planned,
            LastError = error,
            StartedAt = started
        });

    private static string Trim(string message) => message.Length <= 480 ? message : message[..480];
}

public sealed class CollectionBoardSource(IDbContextFactory<AppDbContext> factory) : ICollectionBoardSource
{
    public async Task<CollectionBoard> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var runs = await db.Runs.OrderByDescending(item => item.Id).Take(20)
            .Select(item => new RunRow(item.Profile, item.StartedAt, item.FinishedAt, item.Status, item.Message, item.Requests))
            .ToListAsync(ct);
        var errors = await db.Errors.OrderByDescending(item => item.Id).Take(40)
            .Select(item => new ErrorRow(item.At, item.Kind, item.StatusCode, item.Message))
            .ToListAsync(ct);
        var sliceRows = await db.Slices.OrderByDescending(item => item.Id).Take(30).ToListAsync(ct);
        var slices = sliceRows.Select(item => new SliceRow(item.Key, ((SliceStatus)item.Status).ToString(), item.TotalCount, item.PagesFetched, item.Note)).ToList();
        var pending = await db.Slices.CountAsync(item => item.Status == (int)SliceStatus.Pending || item.Status == (int)SliceStatus.Partial, ct);
        var deferred = await db.Slices.CountAsync(item => item.Status == (int)SliceStatus.Deferred, ct);
        return new CollectionBoard(runs, errors, slices, pending, deferred);
    }
}
