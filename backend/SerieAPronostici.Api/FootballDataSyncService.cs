using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

sealed class FootballDataOptions
{
    public string BaseUrl { get; set; } = "https://api.football-data.org/v4/";
    public string? ApiToken { get; set; }
    public string CompetitionCode { get; set; } = "SA";
    public int SyncIntervalHours { get; set; } = 24;
}

sealed record ImportedFixture(int Id, DateTimeOffset Kickoff, string Round, int HomeTeamId, string HomeTeam, string? HomeCrest, int AwayTeamId, string AwayTeam, string? AwayCrest, string Status, int? HomeGoals, int? AwayGoals);

sealed class FootballDataClient(HttpClient httpClient, IOptions<FootballDataOptions> options, ILogger<FootballDataClient> logger)
{
    private readonly FootballDataOptions _options = options.Value;

    public async Task<IReadOnlyList<ImportedFixture>> GetSeasonFixturesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken)) throw new InvalidOperationException("Configura FootballData__ApiToken prima di richiedere i dati.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"competitions/{Uri.EscapeDataString(_options.CompetitionCode)}/matches");
        request.Headers.Add("X-Auth-Token", _options.ApiToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("La risposta Football-Data.org non contiene l'elenco delle partite atteso.");

        var fixtures = new List<ImportedFixture>();
        foreach (var item in matches.EnumerateArray())
        {
            var home = item.GetProperty("homeTeam");
            var away = item.GetProperty("awayTeam");
            var fullTime = item.GetProperty("score").GetProperty("fullTime");
            var matchday = item.TryGetProperty("matchday", out var day) && day.ValueKind == JsonValueKind.Number ? $"Giornata {day.GetInt32()}" : "Serie A";
            fixtures.Add(new ImportedFixture(
                item.GetProperty("id").GetInt32(),
                DateTimeOffset.Parse(item.GetProperty("utcDate").GetString()!, CultureInfo.InvariantCulture),
                matchday,
                home.GetProperty("id").GetInt32(), home.GetProperty("name").GetString() ?? "", ReadOptionalString(home, "crest"),
                away.GetProperty("id").GetInt32(), away.GetProperty("name").GetString() ?? "", ReadOptionalString(away, "crest"),
                MapStatus(item.GetProperty("status").GetString()),
                ReadNullableInt(fullTime.GetProperty("home")), ReadNullableInt(fullTime.GetProperty("away"))));
        }
        logger.LogInformation("Ricevute {FixtureCount} partite Football-Data.org per {Competition}", fixtures.Count, _options.CompetitionCode);
        return fixtures;
    }

    private static int? ReadNullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var score) ? score : null;
    private static string? ReadOptionalString(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string MapStatus(string? status) => status switch
    {
        "FINISHED" => "FT",
        "IN_PLAY" or "PAUSED" or "EXTRA_TIME" or "PENALTY_SHOOTOUT" => "LIVE",
        "POSTPONED" => "PST",
        "SUSPENDED" => "SUSP",
        "CANCELLED" => "CANC",
        _ => "NS"
    };
}

sealed class FixtureSyncService(IServiceScopeFactory scopeFactory, FootballDataClient footballDataClient, IOptions<FootballDataOptions> options, IConfiguration configuration, ILogger<FixtureSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            logger.LogInformation("Football-Data.org non configurato: il backend mantiene i dati dimostrativi.");
            return;
        }
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("AzureSql")))
        {
            logger.LogWarning("Football-Data.org è configurato ma manca ConnectionStrings__AzureSql: sincronizzazione saltata.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Sincronizzazione Football-Data.org non riuscita; i dati già salvati restano disponibili."); }
            await Task.Delay(TimeSpan.FromHours(Math.Clamp(settings.SyncIntervalHours, 1, 24)), stoppingToken);
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        var imports = await footballDataClient.GetSeasonFixturesAsync(cancellationToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PronosticiDbContext>();
        var existing = await db.Matches.Where(match => match.ExternalFixtureId != null).ToDictionaryAsync(match => match.ExternalFixtureId!.Value, cancellationToken);
        foreach (var fixture in imports)
        {
            if (!existing.TryGetValue(fixture.Id, out var match))
            {
                match = new MatchRecord { ExternalFixtureId = fixture.Id };
                db.Matches.Add(match);
                existing.Add(fixture.Id, match);
            }
            match.HomeTeamId = fixture.HomeTeamId;
            match.AwayTeamId = fixture.AwayTeamId;
            match.HomeCrest = fixture.HomeCrest;
            match.AwayCrest = fixture.AwayCrest;
            match.KickoffUtc = fixture.Kickoff.UtcDateTime;
            match.HomeTeam = fixture.HomeTeam;
            match.AwayTeam = fixture.AwayTeam;
            match.Round = fixture.Round;
            match.FixtureStatus = fixture.Status;
            match.HomeGoals = fixture.HomeGoals;
            match.AwayGoals = fixture.AwayGoals;
            match.Source = "Football-Data.org";
        }
        await db.SaveChangesAsync(cancellationToken);
        var allMatches = await db.Matches.Where(match => match.ExternalFixtureId != null).OrderBy(match => match.KickoffUtc).ToListAsync(cancellationToken);
        foreach (var match in allMatches)
        {
            var priorMatches = allMatches.Where(previous => previous.KickoffUtc < match.KickoffUtc).ToArray();
            ForecastEngine.Update(match, priorMatches);
        }
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sincronizzate {FixtureCount} partite e aggiornati i pronostici", imports.Count);
    }
}

static class ForecastEngine
{
    private static readonly HashSet<string> FinishedStatuses = ["FT"];
    private static readonly (int Start, int End)[] GoalBands = [(0, 1), (0, 2), (1, 2), (1, 3), (1, 4), (2, 3), (2, 4)];

    public static void Update(MatchRecord upcoming, IReadOnlyCollection<MatchRecord> seasonMatches)
    {
        if (upcoming.HomeTeamId is null || upcoming.AwayTeamId is null) return;
        var homeRecent = Recent(upcoming.HomeTeamId.Value, seasonMatches);
        var awayRecent = Recent(upcoming.AwayTeamId.Value, seasonMatches);
        upcoming.HomeForm = Form(homeRecent, upcoming.HomeTeamId.Value);
        upcoming.AwayForm = Form(awayRecent, upcoming.AwayTeamId.Value);
        var homeScored = Average(homeRecent, upcoming.HomeTeamId.Value, true, 1.4);
        var homeConceded = Average(homeRecent, upcoming.HomeTeamId.Value, false, 1.2);
        var awayScored = Average(awayRecent, upcoming.AwayTeamId.Value, true, 1.2);
        var awayConceded = Average(awayRecent, upcoming.AwayTeamId.Value, false, 1.4);
        var expectedHome = Math.Round(Math.Clamp((homeScored + awayConceded) / 2 * 1.08, 0.15, 4.5), 1);
        var expectedAway = Math.Round(Math.Clamp((awayScored + homeConceded) / 2 * 0.94, 0.15, 4.5), 1);
        upcoming.HomeGoalsAverage = (decimal)expectedHome;
        upcoming.AwayGoalsAverage = (decimal)expectedAway;
        var homeDistribution = Poisson(expectedHome);
        var awayDistribution = Poisson(expectedAway);
        var homeWin = 0d; var draw = 0d; var awayWin = 0d;
        for (var h = 0; h < homeDistribution.Length; h++)
        for (var a = 0; a < awayDistribution.Length; a++)
        {
            var probability = homeDistribution[h] * awayDistribution[a];
            if (h > a) homeWin += probability; else if (h == a) draw += probability; else awayWin += probability;
        }
        var pHome = (int)Math.Round(homeWin * 100); var pDraw = (int)Math.Round(draw * 100);
        upcoming.HomeWinProbability = pHome; upcoming.DrawProbability = pDraw; upcoming.AwayWinProbability = 100 - pHome - pDraw;
        var (homeBand, awayBand) = BestHomeAwayBands(homeDistribution, awayDistribution, expectedHome, expectedAway);
        upcoming.HomeGoalBand = homeBand; upcoming.AwayGoalBand = awayBand;
        var total = new double[homeDistribution.Length + awayDistribution.Length - 1];
        for (var h = 0; h < homeDistribution.Length; h++) for (var a = 0; a < awayDistribution.Length; a++) total[h + a] += homeDistribution[h] * awayDistribution[a];
        upcoming.TotalGoalBand = BestSingleBand(total);
    }

    private static List<MatchRecord> Recent(int teamId, IReadOnlyCollection<MatchRecord> matches) => matches.Where(m => FinishedStatuses.Contains(m.FixtureStatus) && m.HomeGoals.HasValue && m.AwayGoals.HasValue && (m.HomeTeamId == teamId || m.AwayTeamId == teamId)).OrderByDescending(m => m.KickoffUtc).Take(5).OrderBy(m => m.KickoffUtc).ToList();
    private static string Form(IReadOnlyList<MatchRecord> matches, int teamId) => matches.Count == 0 ? "—" : string.Join(" ", matches.Select(m => { var scored = m.HomeTeamId == teamId ? m.HomeGoals!.Value : m.AwayGoals!.Value; var conceded = m.HomeTeamId == teamId ? m.AwayGoals!.Value : m.HomeGoals!.Value; return scored > conceded ? "V" : scored == conceded ? "N" : "P"; }));
    private static double Average(IReadOnlyList<MatchRecord> matches, int teamId, bool scored, double fallback) => matches.Count == 0 ? fallback : matches.Average(m => { var isHome = m.HomeTeamId == teamId; return scored ? (isHome ? m.HomeGoals!.Value : m.AwayGoals!.Value) : (isHome ? m.AwayGoals!.Value : m.HomeGoals!.Value); });
    private static double[] Poisson(double lambda) { var values = new double[9]; values[0] = Math.Exp(-lambda); for (var i = 1; i < values.Length; i++) values[i] = values[i - 1] * lambda / i; var sum = values.Sum(); return values.Select(v => v / sum).ToArray(); }
    private static (string Home, string Away) BestHomeAwayBands(IReadOnlyList<double> home, IReadOnlyList<double> away, double expectedHome, double expectedAway)
    {
        var candidates = new List<((int Start, int End) Home, (int Start, int End) Away, double Probability)>();
        foreach (var homeBand in GoalBands)
        foreach (var awayBand in GoalBands)
        {
            if (homeBand.Start == 0 && awayBand.Start == 0) continue;
            var probability = ProbabilityInBand(home, homeBand) * ProbabilityInBand(away, awayBand);
            candidates.Add((homeBand, awayBand, probability));
        }
        var best = candidates.MaxBy(candidate => candidate.Probability);
        return (FormatBand(best.Home), FormatBand(best.Away));
    }

    private static string BestSingleBand(IReadOnlyList<double> distribution) => FormatBand(GoalBands.MaxBy(band => ProbabilityInBand(distribution, band)));
    private static double ProbabilityInBand(IReadOnlyList<double> distribution, (int Start, int End) band) => distribution.Skip(band.Start).Take(band.End - band.Start + 1).Sum();
    private static string FormatBand((int Start, int End) band) => $"{band.Start}–{band.End}";
}
