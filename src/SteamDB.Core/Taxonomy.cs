namespace SteamDB.Core;

public sealed record PriceShelf(string Key, string Name, int? MinCents, int? MaxCents, bool Free);

public sealed record NamedTag(int Id, string Name);

public static class Taxonomy
{
    public static IReadOnlyList<PriceShelf> Shelves { get; } =
    [
        new("free", "Бесплатно", null, 0, true),
        new("le499", "≤ $4.99", 1, 499, false),
        new("to999", "$5.00–$9.99", 500, 999, false),
        new("to1499", "$10.00–$14.99", 1000, 1499, false),
        new("to1999", "$15.00–$19.99", 1500, 1999, false),
        new("to2998", "$20.00–$29.98", 2000, 2998, false),
        new("ge2999", "$29.99+", 2999, null, false)
    ];

    public static IReadOnlyList<NamedTag> MajorGenres { get; } =
    [
        new(19, "Action"),
        new(21, "Adventure"),
        new(597, "Casual"),
        new(492, "Indie"),
        new(122, "RPG"),
        new(599, "Simulation"),
        new(9, "Strategy"),
        new(701, "Sports"),
        new(699, "Racing")
    ];

    public const int SinglePlayerCategory = 2;
    public const int MultiplayerCategory = 1;
    public const int CoopCategory = 9;
    public const int AchievementsCategory = 22;
    public const int CloudCategory = 23;
    public const int WorkshopCategory = 30;
    public const int ControllerCategory = 28;
    public const int GamesCategory = 998;

    public static bool MatchesShelf(GameRecord game, string? shelfKey)
    {
        if (string.IsNullOrWhiteSpace(shelfKey))
            return true;
        var shelf = Shelves.FirstOrDefault(item => item.Key == shelfKey);
        if (shelf is null)
            return true;
        if (shelf.Free)
            return game.IsFree;
        if (game.IsFree || game.PriceCents is null)
            return false;
        var price = game.PriceCents.Value;
        if (shelf.MinCents is int min && price < min)
            return false;
        if (shelf.MaxCents is int max && price > max)
            return false;
        return true;
    }

    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(static character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
