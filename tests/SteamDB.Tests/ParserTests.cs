using SteamDB.Infrastructure.Steam;

namespace SteamDB.Tests;

public class ParserTests
{
    [Fact]
    public void AppDetails_reads_the_public_card()
    {
        var json = """
        {
          "570": {
            "success": true,
            "data": {
              "name": "Sample Game",
              "steam_appid": 570,
              "required_age": 0,
              "is_free": false,
              "short_description": "A short text.",
              "header_image": "https://example.test/header.jpg",
              "developers": ["Sample Dev"],
              "publishers": ["Sample Pub"],
              "price_overview": {"currency": "USD", "initial": 1999, "final": 999, "discount_percent": 50},
              "platforms": {"windows": true, "mac": false, "linux": true},
              "categories": [
                {"id": 2, "description": "Single-player"},
                {"id": 22, "description": "Steam Achievements"},
                {"id": 23, "description": "Steam Cloud"},
                {"id": 28, "description": "Full controller support"}
              ],
              "genres": [{"id": "1", "description": "Action"}, {"id": "70", "description": "Early Access"}],
              "screenshots": [{"id": 1}, {"id": 2}],
              "movies": [{"id": 9}],
              "release_date": {"coming_soon": false, "date": "10 Oct, 2020"},
              "supported_languages": "English<strong>*</strong>, Russian<br>French",
              "demos": [{"appid": 1, "description": ""}],
              "content_descriptors": {"ids": [], "notes": null},
              "steam_deck_compatibility": {"category": 3}
            }
          }
        }
        """;
        var game = SteamParsers.AppDetails(json, 570);
        Assert.NotNull(game);
        Assert.Equal("Sample Game", game!.Name);
        Assert.Equal(999, game.PriceCents);
        Assert.Equal(50, game.DiscountPercent);
        Assert.True(game.Windows && game.Linux && !game.Mac);
        Assert.True(game.SinglePlayer && game.Achievements && game.Cloud && game.Controller);
        Assert.Contains("Action", game.Genres);
        Assert.True(game.EarlyAccess);
        Assert.Equal(2, game.ScreenshotCount);
        Assert.True(game.HasTrailer && game.HasDemo);
        Assert.Equal(3, game.LanguageCount);
        Assert.Equal(new DateOnly(2020, 10, 10), game.ReleaseDate);
        Assert.Equal(3, game.DeckCategory);
        Assert.True(game.CardRead);
        Assert.False(game.Unavailable);
    }

    [Fact]
    public void AppDetails_does_not_pass_an_age_gate()
    {
        var failed = SteamParsers.AppDetails("""{"570":{"success":false}}""", 570);
        Assert.NotNull(failed);
        Assert.True(failed!.Unavailable);
        Assert.False(failed.CardRead);
        var html = "<html><form class=\"agecheck\"><select name=\"ageYear\"></select></form></html>";
        Assert.True(SteamParsers.LooksLikeAgeGate(html));
        Assert.Empty(SteamParsers.StorePageTags(html));
    }

    [Fact]
    public void Reviews_and_search_total_count()
    {
        var reviews = SteamParsers.Reviews("""{"success":1,"query_summary":{"total_positive":80,"total_negative":20,"total_reviews":100}}""");
        Assert.NotNull(reviews);
        Assert.Equal(100, reviews!.Total);
        Assert.Equal(0.8, reviews.Ratio);

        var search = SteamParsers.Search("""
        {"success":1,"total_count":321,"results_html":"<a class=\"search_result_row\" href=\"https://store.steampowered.com/app/570/Sample/\" data-ds-appid=\"570\" data-ds-tagids=\"[19,492]\"><div class=\"search_name\"><span class=\"title\">Sample Game</span></div><div class=\"search_released\">10 Oct, 2020</div><div class=\"search_reviewscore\"><span data-tooltip-html=\"Very Positive<br>80% of the 1,234 user reviews for this game are positive.\"></span></div><div class=\"search_price_discount_combined\" data-price-final=\"999\"></div><span class=\"platform_img win\"></span></a>"}
        """);
        Assert.NotNull(search);
        Assert.Equal(321, search!.TotalCount);
        var hit = Assert.Single(search.Hits);
        Assert.Equal(570, hit.AppId);
        Assert.Equal([19, 492], hit.TagIds);
        Assert.Equal(1234, hit.ReviewCount);
        Assert.Equal(0.8, hit.PositiveRatio);
        Assert.Equal(999, hit.PriceCents);
        Assert.True(hit.Windows);
    }

    [Fact]
    public void Sales_parser_keeps_only_allowed_apps()
    {
        var json = """
        {"response":{"results":[
          {"primary_appid":10,"net_units_sold":8,"line_item_type":"Package"},
          {"primary_appid":99,"net_units_sold":100,"line_item_type":"Package"},
          {"primary_appid":10,"gross_units_activated":2,"line_item_type":"Package","package_sale_type":"Retail"}
        ],"max_id":"5"}}
        """;
        var parsed = SteamParsers.Sales(json, new HashSet<int> { 10 });
        Assert.Equal(10, parsed.Units);
        Assert.Equal(["10", "11"], SteamParsers.PartnerAppIds("""{"response":{"apps":[{"appid":10},{"appid":11}]}}""").Select(id => id.ToString()).ToArray());
    }
}
