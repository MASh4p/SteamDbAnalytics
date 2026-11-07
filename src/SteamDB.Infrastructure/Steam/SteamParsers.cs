using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;

namespace SteamDB.Infrastructure.Steam;

public sealed record ParsedGame
{
    public int AppId { get; init; }
    public string Name { get; init; } = "";
    public string? Url { get; init; }
    public string? HeaderImage { get; init; }
    public string? ShortDescription { get; init; }
    public IReadOnlyList<string> Developers { get; init; } = [];
    public IReadOnlyList<string> Publishers { get; init; } = [];
    public int? ReviewCount { get; init; }
    public int? PositiveReviews { get; init; }
    public double? PositiveRatio { get; init; }
    public double? RecentPositiveRatio { get; init; }
    public bool ReviewsKnown { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? ReleaseYear { get; init; }
    public bool ComingSoon { get; init; }
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
    public bool EarlyAccess { get; init; }
    public int DeckCategory { get; init; }
    public int? LanguageCount { get; init; }
    public int ScreenshotCount { get; init; }
    public bool HasTrailer { get; init; }
    public bool IsAdult { get; init; }
    public bool CardRead { get; init; }
    public bool Unavailable { get; init; }
}

public sealed record ReviewSummary(int Total, int Positive, int Negative, double? Ratio);

public sealed record SearchHit(
    int AppId,
    string Name,
    string Url,
    IReadOnlyList<int> TagIds,
    int? PriceCents,
    bool IsFree,
    string? ReleaseText,
    int? ReviewCount,
    double? PositiveRatio,
    bool Windows,
    bool Mac,
    bool Linux);

public sealed record SearchPage(int TotalCount, IReadOnlyList<SearchHit> Hits);

public sealed record TagName(int Id, string Name);

public sealed record SalesParse(long Units, bool CompletePage, string? NextWatermark);

public static partial class SteamParsers
{
    public static ParsedGame? AppDetails(string json, int appId)
    {
        if (LooksLikeAgeGate(json))
            return new ParsedGame { AppId = appId, Unavailable = true };
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var node)
                && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                node = doc.RootElement.EnumerateObject().Select(static property => property.Value).FirstOrDefault();
            }

            if (node.ValueKind != JsonValueKind.Object)
                return null;
            if (!node.TryGetProperty("success", out var success) || !IsTruthy(success))
                return new ParsedGame { AppId = appId, Unavailable = true };
            if (!node.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new ParsedGame { AppId = appId, Unavailable = true };

            var name = Text(data, "name") ?? "";
            var released = data.TryGetProperty("release_date", out var release) ? release : default;
            var coming = released.ValueKind == JsonValueKind.Object && released.TryGetProperty("coming_soon", out var soon) && IsTruthy(soon);
            var dateText = released.ValueKind == JsonValueKind.Object ? Text(released, "date") : null;
            var (date, year) = ParseRelease(dateText);
            var genres = Descriptions(data, "genres");
            var categories = Descriptions(data, "categories");
            var languages = CountLanguages(Text(data, "supported_languages"));
            var requiredAge = data.TryGetProperty("required_age", out var ageNode) && ageNode.TryGetInt32(out var age) ? age : 0;
            var adultIds = DescriptorIds(data);
            int? price = null;
            var currency = "USD";
            var discount = 0;
            var free = data.TryGetProperty("is_free", out var freeNode) && IsTruthy(freeNode);
            if (data.TryGetProperty("price_overview", out var priceNode) && priceNode.ValueKind == JsonValueKind.Object)
            {
                if (priceNode.TryGetProperty("final", out var final) && final.TryGetInt32(out var cents))
                    price = cents;
                currency = Text(priceNode, "currency") ?? currency;
                if (priceNode.TryGetProperty("discount_percent", out var discountNode) && discountNode.TryGetInt32(out var percent))
                    discount = percent;
            }

            if (free)
                price = 0;

            return new ParsedGame
            {
                AppId = data.TryGetProperty("steam_appid", out var idNode) && idNode.TryGetInt32(out var parsedId) ? parsedId : appId,
                Name = name,
                Url = $"https://store.steampowered.com/app/{appId}",
                HeaderImage = Text(data, "header_image"),
                ShortDescription = Strip(Text(data, "short_description")),
                Developers = Strings(data, "developers"),
                Publishers = Strings(data, "publishers"),
                ReleaseDate = date,
                ReleaseYear = year,
                ComingSoon = coming,
                PriceCents = price,
                Currency = currency,
                DiscountPercent = discount,
                IsFree = free,
                Windows = Flag(data, "platforms", "windows"),
                Mac = Flag(data, "platforms", "mac"),
                Linux = Flag(data, "platforms", "linux"),
                Genres = genres,
                SinglePlayer = Has(categories, "single-player", "singleplayer"),
                Coop = Has(categories, "co-op", "coop"),
                Multiplayer = Has(categories, "multi-player", "multiplayer", "pvp", "mmo"),
                Vr = Has(categories, "vr"),
                Controller = Has(categories, "controller"),
                Achievements = Has(categories, "achievement"),
                Cloud = Has(categories, "cloud"),
                Workshop = Has(categories, "workshop"),
                HasDemo = data.TryGetProperty("demos", out var demos) && demos.ValueKind == JsonValueKind.Array && demos.GetArrayLength() > 0,
                EarlyAccess = genres.Any(static genre => genre.Contains("Early Access", StringComparison.OrdinalIgnoreCase)),
                DeckCategory = Deck(data),
                LanguageCount = languages,
                ScreenshotCount = data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array ? shots.GetArrayLength() : 0,
                HasTrailer = data.TryGetProperty("movies", out var movies) && movies.ValueKind == JsonValueKind.Array && movies.GetArrayLength() > 0,
                IsAdult = requiredAge >= 18 || adultIds.Contains(3),
                CardRead = true
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ReviewSummary? Reviews(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query_summary", out var summary))
                return null;
            var positive = Int(summary, "total_positive");
            var negative = Int(summary, "total_negative");
            var total = Int(summary, "total_reviews");
            if (total <= 0)
                total = positive + negative;
            double? ratio = total > 0 ? positive / (double)total : null;
            return new ReviewSummary(total, positive, negative, ratio);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int? CurrentPlayers(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response))
                return null;
            if (response.TryGetProperty("result", out var result) && result.TryGetInt32(out var code) && code != 1)
                return null;
            return response.TryGetProperty("player_count", out var count) && count.TryGetInt32(out var players) ? players : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static SearchPage? Search(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var total = 0;
            if (doc.RootElement.TryGetProperty("total_count", out var totalNode))
                total = totalNode.ValueKind == JsonValueKind.String && int.TryParse(totalNode.GetString(), out var parsed)
                    ? parsed
                    : totalNode.TryGetInt32(out var number) ? number : 0;
            var html = doc.RootElement.TryGetProperty("results_html", out var htmlNode) ? htmlNode.GetString() ?? "" : "";
            return new SearchPage(total, ParseHits(html));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<TagName> Tags(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var array = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("tags", out var tags) ? tags : default;
            if (array.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<TagName>();
            foreach (var item in array.EnumerateArray())
            {
                var id = Int(item, "tagid");
                var name = Text(item, "name");
                if (id > 0 && !string.IsNullOrWhiteSpace(name))
                    list.Add(new TagName(id, name));
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<int> PartnerAppIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var ids = new List<int>();
            CollectAppIds(doc.RootElement, ids);
            return ids.Distinct().ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ChangedDates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response) || !response.TryGetProperty("dates", out var dates))
                return [];
            return dates.EnumerateArray().Select(static item => item.GetString() ?? "").Where(static item => item.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static SalesParse Sales(string json, IReadOnlySet<int> allowedAppIds)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response))
                return new SalesParse(0, true, null);
            long units = 0;
            if (response.TryGetProperty("results", out var results))
            {
                foreach (var row in results.EnumerateArray())
                {
                    var appId = Int(row, "primary_appid");
                    if (appId == 0)
                        appId = Int(row, "appid");
                    if (!allowedAppIds.Contains(appId))
                        continue;
                    if (row.TryGetProperty("net_units_sold", out _))
                        units += Int(row, "net_units_sold");
                    else
                        units += Int(row, "gross_units_activated");
                }
            }

            var watermark = response.TryGetProperty("max_id", out var max) ? max.ToString().Trim('"') : null;
            return new SalesParse(units, true, watermark);
        }
        catch (JsonException)
        {
            return new SalesParse(0, false, null);
        }
    }

    public static (int Adds, int Deletes, int Purchases, int Gifts)? Wishlist(string json, int appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response))
                return null;
            if (response.TryGetProperty("appid", out var idNode) && idNode.TryGetInt32(out var returned) && returned != appId)
                return null;
            if (!response.TryGetProperty("wishlist_summary", out var summary))
                return null;
            return (Int(summary, "wishlist_adds"), Int(summary, "wishlist_deletes"), Int(summary, "wishlist_purchases"), Int(summary, "wishlist_gifts"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> StorePageTags(string html)
    {
        if (LooksLikeAgeGate(html))
            return [];
        var names = new List<string>();
        foreach (Match match in AppTagRegex().Matches(html))
        {
            var name = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        return names;
    }

    public static bool LooksLikeAgeGate(string body) =>
        body.Contains("agecheck", StringComparison.OrdinalIgnoreCase)
        || body.Contains("name=\"ageYear\"", StringComparison.OrdinalIgnoreCase)
        || body.Contains("name=\"ageDay\"", StringComparison.OrdinalIgnoreCase);

    public static (DateOnly? Date, int? Year) ParseRelease(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);
        var cleaned = text.Trim();
        var culture = CultureInfo.GetCultureInfo("en-US");
        string[] formats = ["d MMM, yyyy", "d MMM yyyy", "MMM d, yyyy", "MMM d yyyy", "MMMM d, yyyy", "yyyy"];
        if (DateOnly.TryParseExact(cleaned, formats, culture, DateTimeStyles.None, out var date))
            return (date, date.Year);
        var yearMatch = YearRegex().Match(cleaned);
        if (yearMatch.Success && int.TryParse(yearMatch.Value, out var year))
            return (null, year);
        return (null, null);
    }

    public static int? CountLanguages(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var text = BreakRegex().Replace(html, ",");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        var parts = text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => part.Replace("*", "", StringComparison.Ordinal).Trim())
            .Where(static part => part.Length > 1 && !part.Contains("audio", StringComparison.OrdinalIgnoreCase) && !part.Contains("language", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return parts.Count == 0 ? null : parts.Count;
    }

    private static List<SearchHit> ParseHits(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];
        var context = BrowsingContext.New(Configuration.Default);
        var document = context.OpenAsync(request => request.Content(html)).GetAwaiter().GetResult();
        var hits = new List<SearchHit>();
        foreach (var row in document.QuerySelectorAll("a.search_result_row"))
        {
            var idText = row.GetAttribute("data-ds-appid");
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId))
                continue;
            var tags = ParseTagIds(row.GetAttribute("data-ds-tagids"));
            var title = row.QuerySelector(".title")?.TextContent.Trim() ?? "";
            var url = row.GetAttribute("href") ?? $"https://store.steampowered.com/app/{appId}";
            var priceText = row.GetAttribute("data-price-final")
                ?? row.QuerySelector("[data-price-final]")?.GetAttribute("data-price-final");
            int? price = int.TryParse(priceText, out var cents) ? cents : null;
            var visiblePrice = row.TextContent;
            var free = visiblePrice.Contains("Free", StringComparison.OrdinalIgnoreCase);
            if (free)
                price = 0;
            var released = row.QuerySelector(".search_released")?.TextContent.Trim();
            var tooltip = row.QuerySelector("[data-tooltip-html]")?.GetAttribute("data-tooltip-html") ?? "";
            var reviewCount = ReviewCountFromTooltip(tooltip);
            var ratio = RatioFromTooltip(tooltip);
            hits.Add(new SearchHit(
                appId,
                WebUtility.HtmlDecode(title),
                url,
                tags,
                price,
                free,
                released,
                reviewCount,
                ratio,
                row.QuerySelector(".platform_img.win") is not null || row.InnerHtml.Contains("platform_img win", StringComparison.Ordinal),
                row.QuerySelector(".platform_img.mac") is not null || row.InnerHtml.Contains("platform_img mac", StringComparison.Ordinal),
                row.QuerySelector(".platform_img.linux") is not null || row.InnerHtml.Contains("platform_img linux", StringComparison.Ordinal)));
        }

        return hits;
    }

    private static int? ReviewCountFromTooltip(string tooltip)
    {
        var match = ReviewCountRegex().Match(WebUtility.HtmlDecode(tooltip));
        if (!match.Success)
            return null;
        var digits = match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        return int.TryParse(digits, out var count) ? count : null;
    }

    private static double? RatioFromTooltip(string tooltip)
    {
        var match = RatioRegex().Match(WebUtility.HtmlDecode(tooltip));
        return match.Success && int.TryParse(match.Groups[1].Value, out var percent) ? percent / 100.0 : null;
    }

    private static List<int> ParseTagIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        var ids = new List<int>();
        foreach (Match match in NumberRegex().Matches(raw))
        {
            if (int.TryParse(match.Value, out var id))
                ids.Add(id);
        }

        return ids;
    }

    private static void CollectAppIds(JsonElement element, List<int> ids)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("appid", out var idNode) && idNode.TryGetInt32(out var id))
                ids.Add(id);
            foreach (var property in element.EnumerateObject())
                CollectAppIds(property.Value, ids);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectAppIds(item, ids);
        }
    }

    private static int Deck(JsonElement data)
    {
        if (!data.TryGetProperty("steam_deck_compatibility", out var deck))
            return 0;
        var category = Int(deck, "category");
        return category is >= 0 and <= 3 ? category : 0;
    }

    private static List<int> DescriptorIds(JsonElement data)
    {
        if (!data.TryGetProperty("content_descriptors", out var descriptors) || !descriptors.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return [];
        return ids.EnumerateArray().Where(static item => item.TryGetInt32(out _)).Select(static item => item.GetInt32()).ToList();
    }

    private static bool Has(IEnumerable<string> values, params string[] needles) =>
        values.Any(value => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase)));

    private static bool Flag(JsonElement data, string objectName, string property) =>
        data.TryGetProperty(objectName, out var node) && node.TryGetProperty(property, out var flag) && IsTruthy(flag);

    private static List<string> Descriptions(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Select(static item => Text(item, "description")).Where(static item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToList();
    }

    private static List<string> Strings(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Select(static item => item.GetString() ?? "").Where(static item => item.Length > 0).ToList();
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return 0;
    }

    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
        _ => false
    };

    private static string? Strip(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;
        return WebUtility.HtmlDecode(TagRegex().Replace(html, "")).Trim();
    }

    [GeneratedRegex(@"class=""app_tag""[^>]*>([^<]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AppTagRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex("<.*?>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"([\d][\d,\.]*)\s+user reviews", RegexOptions.IgnoreCase)]
    private static partial Regex ReviewCountRegex();

    [GeneratedRegex(@"(\d+)\s*%")]
    private static partial Regex RatioRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();
}
