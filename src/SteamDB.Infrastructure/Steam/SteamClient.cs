using System.Globalization;
using System.Text.Json;

namespace SteamDB.Infrastructure.Steam;

public sealed record StoreQuery
{
    public IReadOnlyList<int> TagIds { get; init; } = [];
    public int? MaxPriceCents { get; init; }
    public string? Os { get; init; }
    public int? Category2 { get; init; }
    public int? Deck { get; init; }
    public string Sort { get; init; } = "_ASC";
    public string? Filter { get; init; }
}

public static class SteamUrls
{
    public const string UserAgent = "SteamDBAnalytics/1.0 (personal local research)";

    public static string AppDetails(int appId) =>
        $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=us&l=english";

    public static string Reviews(int appId, string filter) =>
        $"https://store.steampowered.com/appreviews/{appId}?json=1&filter={filter}&language=all&review_type=all&purchase_type=all&num_per_page=0";

    public static string Players(int appId) =>
        $"https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={appId}";

    public static string PopularTags() => "https://store.steampowered.com/tagdata/populartags/english";

    public static string AppPage(int appId) => $"https://store.steampowered.com/app/{appId}/?l=english&cc=us";

    public static string Search(StoreQuery query, int start, int count)
    {
        var parts = new List<string>
        {
            "json=1",
            "infinite=1",
            "ignore_preferences=1",
            "cc=us",
            "l=english",
            "category1=998",
            "start=" + start.ToString(CultureInfo.InvariantCulture),
            "count=" + count.ToString(CultureInfo.InvariantCulture),
            "sort_by=" + Uri.EscapeDataString(query.Sort)
        };
        if (query.TagIds.Count > 0)
            parts.Add("tags=" + string.Join(',', query.TagIds));
        if (query.MaxPriceCents is int price)
            parts.Add("maxprice=" + price.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(query.Os))
            parts.Add("os=" + Uri.EscapeDataString(query.Os));
        if (query.Category2 is int category)
            parts.Add("category2=" + category.ToString(CultureInfo.InvariantCulture));
        if (query.Deck is int deck)
            parts.Add("deck_compatibility=" + deck.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(query.Filter))
            parts.Add("filter=" + Uri.EscapeDataString(query.Filter));
        return "https://store.steampowered.com/search/results/?" + string.Join('&', parts);
    }

    public static string PartnerAppList() =>
        "https://partner.steam-api.com/ISteamApps/GetPartnerAppListForWebAPIKey/v2/";

    public static string ChangedDates() =>
        "https://partner.steam-api.com/IPartnerFinancialsService/GetChangedDatesForPartner/v001/?highwatermark=0";

    public static string DetailedSales(string date, string watermark) =>
        "https://partner.steam-api.com/IPartnerFinancialsService/GetDetailedSales/v001/?date="
        + Uri.EscapeDataString(date) + "&highwatermark_id=" + Uri.EscapeDataString(watermark);

    public static string Wishlist(int appId, string date) =>
        "https://partner.steam-api.com/IPartnerFinancialsService/GetAppWishlistReporting/v001/?appid="
        + appId.ToString(CultureInfo.InvariantCulture) + "&date=" + Uri.EscapeDataString(date);
}

public sealed record SteamFetch(bool Ok, bool Stop, int StatusCode, string Body, string Message);

public sealed class PoliteSteamClient
{
    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly object _pace = new();
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public PoliteSteamClient(HttpClient http, ISettingsStore settings)
    {
        _http = http;
        _settings = settings;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(SteamUrls.UserAgent);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public bool IsPaused { get; private set; }
    public string? PauseReason { get; private set; }

    public void Resume()
    {
        IsPaused = false;
        PauseReason = null;
    }

    public async Task<SteamFetch> GetAsync(string url, CancellationToken ct, string? protectedKey = null)
    {
        if (url.Contains("key=", StringComparison.OrdinalIgnoreCase))
            return new SteamFetch(false, true, 0, "", "Ключ нельзя передавать в адресе.");

        var settings = _settings.Current;
        var permits = Math.Clamp(settings.MaxConcurrency, 1, 2) == 1 ? 2 : 1;
        var acquired = 0;
        try
        {
            await _slots.WaitAsync(ct);
            acquired++;
            if (permits == 2)
            {
                await _slots.WaitAsync(ct);
                acquired++;
            }

            TimeSpan wait;
            var jitter = settings.JitterMs > 0 ? Random.Shared.Next(0, settings.JitterMs + 1) : 0;
            lock (_pace)
            {
                wait = _next - DateTimeOffset.UtcNow;
                if (wait < TimeSpan.Zero)
                    wait = TimeSpan.Zero;
                _next = DateTimeOffset.UtcNow + wait + TimeSpan.FromMilliseconds(Math.Max(0, settings.MinDelayMs) + jitter);
            }

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            if (jitter > 0)
                await Task.Delay(jitter, ct);

            var retries = Math.Max(1, settings.MaxRetries);
            SteamFetch? last = null;
            for (var attempt = 1; attempt <= retries; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(protectedKey))
                    request.Headers.TryAddWithoutValidation("x-webapi-key", protectedKey);
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, ct);
                }
                catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
                {
                    last = new SteamFetch(false, false, 0, "", "Сеть: " + ex.Message);
                    if (attempt == retries)
                        break;
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    continue;
                }

                var code = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);
                if (code is 429 or 403 || code >= 500)
                {
                    var reason = code switch
                    {
                        429 => "Steam ответил 429: слишком часто. Пауза и повтор.",
                        403 => "Steam ответил 403: запрос отклонён, обход не выполняется.",
                        _ => $"Steam ответил {code}."
                    };
                    last = new SteamFetch(false, false, code, "", reason);
                    if (attempt == retries)
                        break;
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return new SteamFetch(false, false, code, body, $"Ответ {code} без повтора.");
                return new SteamFetch(true, false, code, body, "OK");
            }

            IsPaused = true;
            PauseReason = (last?.Message ?? "Повторы исчерпаны.") + " Сбор остановлен.";
            return new SteamFetch(false, true, last?.StatusCode ?? 0, "", PauseReason);
        }
        finally
        {
            if (acquired > 0)
                _slots.Release(acquired);
        }
    }
}

public static class JsonWork
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Of<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
