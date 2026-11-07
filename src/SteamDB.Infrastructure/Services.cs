using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SteamDB.Core;
using SteamDB.Infrastructure.Data;
using SteamDB.Infrastructure.Steam;

namespace SteamDB.Infrastructure;

public sealed class TableExport : ITableExport
{
    public byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine(string.Join(';', headers.Select(Escape)));
        foreach (var row in rows)
            builder.AppendLine(string.Join(';', row.Select(value => Escape(value ?? ""))));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public byte[] Excel(string sheet, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        using var workbook = new XLWorkbook();
        var grid = workbook.Worksheets.Add(string.IsNullOrWhiteSpace(sheet) ? "Данные" : sheet[..Math.Min(sheet.Length, 31)]);
        for (var column = 0; column < headers.Count; column++)
            grid.Cell(1, column + 1).Value = headers[column];
        var rowIndex = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Count; column++)
                grid.Cell(rowIndex, column + 1).Value = row[column] ?? "";
            rowIndex++;
        }

        grid.SheetView.FreezeRows(1);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static string Escape(string value)
    {
        var text = value.Replace("\"", "\"\"");
        return text.Contains(';') || text.Contains('"') || text.Contains('\n') ? "\"" + text + "\"" : text;
    }
}

public sealed class IdeaSearch(PoliteSteamClient client) : IIdeaSearch
{
    public async Task<LiveCount> CountAsync(IdeaRequest request, CancellationToken ct = default)
    {
        if (client.IsPaused)
            return new LiveCount(null, client.PauseReason, null);
        var tags = request.TagIds.ToList();
        string? note = null;
        if (request.PriceShelfKey == "free" && !tags.Contains(113))
            tags.Add(113);
        var shelf = Taxonomy.Shelves.FirstOrDefault(item => item.Key == request.PriceShelfKey);
        if (shelf is { Free: false, MinCents: > 0 })
            note = "У поиска Steam нет нижней границы цены, живой счётчик может включать более дешёвые игры.";
        var query = new StoreQuery
        {
            TagIds = tags,
            Os = request.Platform switch
            {
                "windows" => "win",
                "mac" => "mac",
                "linux" => "linux",
                _ => null
            },
            MaxPriceCents = shelf is { Free: false, MaxCents: int max } ? max : null,
            Category2 = request.Multiplayer == true ? Taxonomy.MultiplayerCategory : null,
            Deck = request.Deck switch
            {
                DeckStatus.Verified => 3,
                DeckStatus.Playable => 2,
                DeckStatus.Unsupported => 1,
                _ => null
            }
        };
        var fetch = await client.GetAsync(SteamUrls.Search(query, 0, 1), ct);
        if (!fetch.Ok)
            return new LiveCount(null, fetch.Message, note);
        var page = SteamParsers.Search(fetch.Body);
        if (page is null)
            return new LiveCount(null, "Ответ поиска не разобран. Показана оценка по базе.", note);
        return new LiveCount(page.TotalCount, null, note);
    }
}

public sealed class MyGames(
    IDbContextFactory<AppDbContext> factory,
    ISettingsStore settings,
    SecretProtector protector,
    PoliteSteamClient client,
    IDatasetCache cache) : IMyGames
{
    public async Task<IReadOnlyList<int>> TrackedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Tracked.OrderBy(item => item.AppId).Select(item => item.AppId).ToListAsync(ct);
    }

    public async Task AddAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Tracked.AnyAsync(item => item.AppId == appId, ct))
        {
            db.Tracked.Add(new TrackedEntity { AppId = appId, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RemoveAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Tracked.FirstOrDefaultAsync(item => item.AppId == appId, ct);
        if (row is null)
            return;
        db.Tracked.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> HasKeyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Secrets.AnyAsync(item => item.Name == "steam-web-api", ct);
    }

    public async Task SaveKeyAsync(string key, CancellationToken ct = default)
    {
        var trimmed = key.Trim();
        if (trimmed.Length < 8)
            throw new InvalidOperationException("Ключ слишком короткий.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Secrets.FirstOrDefaultAsync(item => item.Name == "steam-web-api", ct);
        var cipher = protector.Protect(trimmed);
        if (row is null)
            db.Secrets.Add(new SecretEntity { Name = "steam-web-api", Cipher = cipher });
        else
            row.Cipher = cipher;
        await db.SaveChangesAsync(ct);
    }

    public async Task ForgetKeyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Secrets.FirstOrDefaultAsync(item => item.Name == "steam-web-api", ct);
        if (row is null)
            return;
        db.Secrets.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<CalibrationOffer> CalibrateAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var secret = await db.Secrets.FirstOrDefaultAsync(item => item.Name == "steam-web-api", ct);
        if (secret is null)
            return new CalibrationOffer(false, "Ключ не сохранён. Калибровка остаётся на доле по умолчанию.", null);
        string key;
        try
        {
            key = protector.Unprotect(secret.Cipher);
        }
        catch (Exception)
        {
            return new CalibrationOffer(false, "Сохранённый ключ не читается. Сохраните его снова.", null);
        }

        var tracked = await db.Tracked.Select(item => item.AppId).ToListAsync(ct);
        if (tracked.Count == 0)
            return new CalibrationOffer(false, "Сначала добавьте свои appid. Чужие приложения ключом не запрашиваются.", null);

        var list = await client.GetAsync(SteamUrls.PartnerAppList(), ct, key);
        if (list.Stop || list.StatusCode is 401 or 403)
            return new CalibrationOffer(false, "Ключ не открыл статистику своих приложений (нужен финансовый ключ издателя). Показы и визиты этот API не отдаёт. Калибровка остаётся на доле 2%.", null);
        if (!list.Ok)
            return new CalibrationOffer(false, list.Message, null);

        var owned = SteamParsers.PartnerAppIds(list.Body).ToHashSet();
        var allowed = tracked.Where(owned.Contains).ToHashSet();
        if (allowed.Count == 0)
            return new CalibrationOffer(false, "Ни один отслеживаемый appid не входит в приложения этого ключа. Остальные appid не запрашиваются.", null);

        var datesFetch = await client.GetAsync(SteamUrls.ChangedDates(), ct, key);
        if (!datesFetch.Ok)
            return new CalibrationOffer(false, datesFetch.Message, null);
        var dates = SteamParsers.ChangedDates(datesFetch.Body);
        if (dates.Count == 0 || dates.Count > 60)
            return new CalibrationOffer(false, "Ключ принят, но полная сумма продаж не загружается: дат слишком много или их нет. Короткое окно не подставляется как «владельцы». Показы и визиты API не отдаёт.", null);

        long units = 0;
        foreach (var date in dates)
        {
            var watermark = "0";
            for (var page = 0; page < 20; page++)
            {
                var sales = await client.GetAsync(SteamUrls.DetailedSales(date, watermark), ct, key);
                if (!sales.Ok)
                    return new CalibrationOffer(false, "Продажи своих игр прочитаны не целиком, отношение не предлагается. " + sales.Message, null);
                var parsed = SteamParsers.Sales(sales.Body, allowed);
                if (!parsed.CompletePage)
                    return new CalibrationOffer(false, "Ответ продаж не разобран, калибровка не предложена.", null);
                units += parsed.Units;
                if (string.IsNullOrWhiteSpace(parsed.NextWatermark) || parsed.NextWatermark == watermark)
                    break;
                watermark = parsed.NextWatermark;
            }
        }

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");
        foreach (var appId in allowed)
        {
            var wish = await client.GetAsync(SteamUrls.Wishlist(appId, yesterday), ct, key);
            if (!wish.Ok)
                continue;
            var flow = SteamParsers.Wishlist(wish.Body, appId);
            if (flow is null)
                continue;
            db.OwnStats.Add(new OwnStatEntity
            {
                AppId = appId,
                Date = DateOnly.Parse(yesterday),
                WishlistAdds = flow.Value.Adds,
                WishlistDeletes = flow.Value.Deletes,
                WishlistPurchases = flow.Value.Purchases,
                NetUnits = null
            });
        }

        var reviews = await db.Games.Where(game => allowed.Contains(game.AppId) && game.ReviewCount != null).SumAsync(game => game.ReviewCount ?? 0, ct);
        await db.SaveChangesAsync(ct);
        if (reviews <= 0 || units <= 0)
            return new CalibrationOffer(false, "По своим играм нет пары «единицы и отзывы». Показы и визиты API не отдаёт, доля 2% сохраняется.", null);
        var ratio = units / (double)reviews;
        return new CalibrationOffer(true, $"По своим играм выходит {ratio:0.##} единиц на отзыв. Показы и визиты в ответе API отсутствуют. Можно применить это ко всем оценкам охвата.", ratio);
    }

    public async Task ApplyCalibrationAsync(double unitsPerReview, CancellationToken ct = default)
    {
        if (unitsPerReview <= 0)
            return;
        await settings.UpdateAsync(item => item.CalibratedUnitsPerReview = unitsPerReview, ct);
        await cache.ReloadAsync(ct);
    }
}

public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("SteamDB.Analytics.OwnKey.v1");

    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string value) => _protector.Unprotect(value);
}
