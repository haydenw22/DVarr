namespace DVarr.Services.Events;

public sealed record CatalogLeague(string Id, string Name, string Sport);

/// <summary>
/// A curated catalogue of popular TheSportsDB leagues with verified ids. The free API key only exposes a tiny
/// SAMPLE via the browse endpoints (all_sports → 2 sports, search_all_leagues → ~5/sport, missing F1/AFL/etc.),
/// but LOOKUP + EVENTS by league id work for everything on the free key. So the UI picks from this bundled list
/// and DVarr fetches events/artwork by id — no premium key needed. (Anything missing can be added by pasting a
/// raw TheSportsDB league id in the UI.) Ids verified live against lookupleague.php.
/// </summary>
public static class LeagueCatalog
{
    public static readonly IReadOnlyList<CatalogLeague> All = new CatalogLeague[]
    {
        // Australian Football
        new("4456", "AFL — Australian Football League", "Australian Football"),
        new("5311", "AFL Women's (AFLW)", "Australian Football"),
        // Motorsport
        new("4370", "Formula 1", "Motorsport"),
        new("4486", "Formula 2", "Motorsport"),
        new("4487", "Formula 3", "Motorsport"),
        new("4371", "Formula E", "Motorsport"),
        new("4407", "MotoGP", "Motorsport"),
        new("4436", "Moto2", "Motorsport"),
        new("4437", "Moto3", "Motorsport"),
        new("4489", "V8 Supercars", "Motorsport"),
        new("4413", "WEC — World Endurance Championship", "Motorsport"),
        new("4393", "NASCAR Cup Series", "Motorsport"),
        new("4373", "IndyCar Series", "Motorsport"),
        new("4409", "World Rally Championship (WRC)", "Motorsport"),
        new("4372", "BTCC — British Touring Cars", "Motorsport"),
        new("4454", "Superbike World Championship (SBK)", "Motorsport"),
        new("4587", "MXGP — Motocross", "Motorsport"),
        new("4438", "DTM", "Motorsport"),
        new("4447", "Dakar Rally", "Motorsport"),
        // Soccer
        new("4429", "FIFA World Cup", "Soccer"),
        new("4499", "Copa America", "Soccer"),
        new("4356", "Australian A-League", "Soccer"),
        new("4328", "English Premier League", "Soccer"),
        new("4329", "English League Championship", "Soccer"),
        new("4480", "UEFA Champions League", "Soccer"),
        new("4481", "UEFA Europa League", "Soccer"),
        new("4335", "Spanish La Liga", "Soccer"),
        new("4332", "Italian Serie A", "Soccer"),
        new("4331", "German Bundesliga", "Soccer"),
        new("4334", "French Ligue 1", "Soccer"),
        new("4346", "USA Major League Soccer (MLS)", "Soccer"),
        // Rugby League
        new("4416", "NRL — National Rugby League", "Rugby League"),
        new("4415", "Super League", "Rugby League"),
        new("5835", "State of Origin", "Rugby League"),
        new("5806", "Rugby League World Cup", "Rugby League"),
        new("5834", "World Club Challenge", "Rugby League"),
        // Rugby Union
        new("4551", "Super Rugby", "Rugby Union"),
        new("4714", "Six Nations", "Rugby Union"),
        new("4574", "Rugby World Cup", "Rugby Union"),
        new("4986", "The Rugby Championship", "Rugby Union"),
        new("4446", "United Rugby Championship", "Rugby Union"),
        new("4430", "French Top 14", "Rugby Union"),
        new("4414", "English Premiership Rugby", "Rugby Union"),
        new("4550", "European Rugby Champions Cup", "Rugby Union"),
        // Basketball
        new("4387", "NBA", "Basketball"),
        new("4607", "NCAA Basketball (Men's)", "Basketball"),
        // American Football
        new("4391", "NFL", "American Football"),
        // Baseball
        new("4424", "MLB", "Baseball"),
        // Ice Hockey
        new("4380", "NHL", "Ice Hockey"),
    };

    public static IEnumerable<string> Sports() => All.Select(l => l.Sport).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
    public static IEnumerable<CatalogLeague> BySport(string sport)
        => All.Where(l => string.Equals(l.Sport, sport, StringComparison.OrdinalIgnoreCase)).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase);
}
