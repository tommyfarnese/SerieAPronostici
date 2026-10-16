using Microsoft.EntityFrameworkCore;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
var frontendOrigin = builder.Configuration["FrontendOrigin"] ?? "http://localhost:5173";
var timeZoneId = builder.Configuration["TimeZoneId"] ?? "Europe/Rome";
var footballDataToken = builder.Configuration["FootballData:ApiToken"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(frontendOrigin).AllowAnyHeader().AllowAnyMethod()));
builder.Services.Configure<FootballDataOptions>(builder.Configuration.GetSection("FootballData"));
builder.Services.AddHttpClient<FootballDataClient>((services, client) =>
{
    var settings = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FootballDataOptions>>().Value;
    client.BaseAddress = new Uri(settings.BaseUrl);
});
builder.Services.AddHostedService<FixtureSyncService>();
var connectionString = builder.Configuration.GetConnectionString("AzureSql");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<PronosticiDbContext>(options => options.UseSqlServer(connectionString));
}
var app = builder.Build();
app.UseCors();
if (!string.IsNullOrWhiteSpace(connectionString))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PronosticiDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("IF COL_LENGTH(N'dbo.Matches', N'HomeCrest') IS NULL ALTER TABLE dbo.Matches ADD HomeCrest nvarchar(300) NULL; IF COL_LENGTH(N'dbo.Matches', N'AwayCrest') IS NULL ALTER TABLE dbo.Matches ADD AwayCrest nvarchar(300) NULL;");
    if (!await db.Matches.AnyAsync() && string.IsNullOrWhiteSpace(footballDataToken))
    {
        db.Matches.AddRange(DemoData.Records);
        await db.SaveChangesAsync();
    }
}
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", provider = string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(footballDataToken) ? "Demo" : "Football-Data.org + AzureSql" }));
app.MapGet("/api/matches", async (IServiceScopeFactory scopeFactory) =>
{
    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(footballDataToken)) return (IResult)Results.Ok(DemoData.Matches);
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PronosticiDbContext>();
    var matches = await db.Matches.AsNoTracking().Where(match => match.ExternalFixtureId != null && match.KickoffUtc >= DateTime.UtcNow.AddDays(-1)).OrderBy(match => match.KickoffUtc).ToListAsync();
    return (IResult)Results.Ok(matches.Select(match => ToMatch(match, timeZoneId)));
});
app.MapGet("/api/history", async (IServiceScopeFactory scopeFactory) =>
{
    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(footballDataToken)) return Results.Ok(Array.Empty<Match>());
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PronosticiDbContext>();
    var matches = await db.Matches.AsNoTracking()
        .Where(match => match.ExternalFixtureId != null && match.FixtureStatus == "FT" && match.HomeGoals != null && match.AwayGoals != null)
        .OrderByDescending(match => match.KickoffUtc)
        .ToListAsync();
    return Results.Ok(matches.Select(match => ToMatch(match, timeZoneId)));
});
app.MapGet("/api/matches/{id:int}/details", async (int id, IServiceScopeFactory scopeFactory) =>
{
    if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(footballDataToken)) return Results.NotFound();
    await using var scope = scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PronosticiDbContext>();
    var match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.ExternalFixtureId != null);
    if (match is null) return Results.NotFound();
    var homeRecentRows = await db.Matches.AsNoTracking()
        .Where(item => item.FixtureStatus == "FT" && item.HomeGoals != null && item.AwayGoals != null && item.KickoffUtc < match.KickoffUtc && (item.HomeTeamId == match.HomeTeamId || item.AwayTeamId == match.HomeTeamId))
        .OrderByDescending(item => item.KickoffUtc).Take(5)
        .Select(item => new { item.KickoffUtc, Opponent = item.HomeTeamId == match.HomeTeamId ? item.AwayTeam : item.HomeTeam, GoalsFor = item.HomeTeamId == match.HomeTeamId ? item.HomeGoals!.Value : item.AwayGoals!.Value, GoalsAgainst = item.HomeTeamId == match.HomeTeamId ? item.AwayGoals!.Value : item.HomeGoals!.Value, AtHome = item.HomeTeamId == match.HomeTeamId })
        .ToListAsync();
    var homeRecent = homeRecentRows.Select(item => new RecentMatch(FormatKickoff(item.KickoffUtc, timeZoneId), item.Opponent, item.GoalsFor, item.GoalsAgainst, item.AtHome)).ToList();
    var awayRecentRows = await db.Matches.AsNoTracking()
        .Where(item => item.FixtureStatus == "FT" && item.HomeGoals != null && item.AwayGoals != null && item.KickoffUtc < match.KickoffUtc && (item.HomeTeamId == match.AwayTeamId || item.AwayTeamId == match.AwayTeamId))
        .OrderByDescending(item => item.KickoffUtc).Take(5)
        .Select(item => new { item.KickoffUtc, Opponent = item.HomeTeamId == match.AwayTeamId ? item.AwayTeam : item.HomeTeam, GoalsFor = item.HomeTeamId == match.AwayTeamId ? item.HomeGoals!.Value : item.AwayGoals!.Value, GoalsAgainst = item.HomeTeamId == match.AwayTeamId ? item.AwayGoals!.Value : item.HomeGoals!.Value, AtHome = item.HomeTeamId == match.AwayTeamId })
        .ToListAsync();
    var awayRecent = awayRecentRows.Select(item => new RecentMatch(FormatKickoff(item.KickoffUtc, timeZoneId), item.Opponent, item.GoalsFor, item.GoalsAgainst, item.AtHome)).ToList();
    return Results.Ok(new MatchDetails(ToMatch(match, timeZoneId), homeRecent, awayRecent));
});
app.Run();

static string FormatKickoff(DateTime kickoffUtc, string timeZoneId)
{
    var utc = DateTime.SpecifyKind(kickoffUtc, DateTimeKind.Utc);
    try
    {
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId))
            .ToString("ddd dd MMM · HH:mm", CultureInfo.GetCultureInfo("it-IT"));
    }
    catch (TimeZoneNotFoundException)
    {
        return utc.ToString("ddd dd MMM · HH:mm", CultureInfo.GetCultureInfo("it-IT"));
    }
}

static Match ToMatch(MatchRecord match, string timeZoneId) => new(
    match.HomeTeam, match.AwayTeam, FormatKickoff(match.KickoffUtc, timeZoneId), match.Round, match.HomeForm, match.AwayForm,
    match.HomeGoalsAverage.ToString("0.0", CultureInfo.InvariantCulture), match.AwayGoalsAverage.ToString("0.0", CultureInfo.InvariantCulture),
    new(match.HomeWinProbability, match.DrawProbability, match.AwayWinProbability), new(match.HomeGoalBand, match.AwayGoalBand, match.TotalGoalBand),
    match.Id, match.HomeCrest, match.AwayCrest, match.HomeGoals, match.AwayGoals);

sealed class PronosticiDbContext(DbContextOptions<PronosticiDbContext> options) : DbContext(options)
{
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MatchRecord>(entity =>
        {
            entity.ToTable("Matches");
            entity.HasKey(match => match.Id);
            entity.Property(match => match.HomeTeam).HasMaxLength(80).IsRequired();
            entity.Property(match => match.AwayTeam).HasMaxLength(80).IsRequired();
            entity.Property(match => match.Round).HasMaxLength(40).IsRequired();
            entity.Property(match => match.HomeForm).HasMaxLength(15).IsRequired();
            entity.Property(match => match.AwayForm).HasMaxLength(15).IsRequired();
            entity.Property(match => match.HomeGoalBand).HasMaxLength(10).IsRequired();
            entity.Property(match => match.AwayGoalBand).HasMaxLength(10).IsRequired();
            entity.Property(match => match.TotalGoalBand).HasMaxLength(10).IsRequired();
            entity.Property(match => match.HomeGoalsAverage).HasPrecision(4, 1);
            entity.Property(match => match.AwayGoalsAverage).HasPrecision(4, 1);
            entity.Property(match => match.Source).HasMaxLength(40).IsRequired();
            entity.Property(match => match.HomeCrest).HasMaxLength(300);
            entity.Property(match => match.AwayCrest).HasMaxLength(300);
            entity.HasIndex(match => match.KickoffUtc);
            entity.HasIndex(match => match.ExternalFixtureId).IsUnique().HasFilter("[ExternalFixtureId] IS NOT NULL");
        });
    }
}

sealed class MatchRecord
{
    public int Id { get; set; }
    public int? ExternalFixtureId { get; set; }
    public int? HomeTeamId { get; set; }
    public int? AwayTeamId { get; set; }
    public string? HomeCrest { get; set; }
    public string? AwayCrest { get; set; }
    public DateTime KickoffUtc { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public string Round { get; set; } = "";
    public string HomeForm { get; set; } = "";
    public string AwayForm { get; set; } = "";
    public decimal HomeGoalsAverage { get; set; }
    public decimal AwayGoalsAverage { get; set; }
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
    public int HomeWinProbability { get; set; }
    public int DrawProbability { get; set; }
    public int AwayWinProbability { get; set; }
    public string HomeGoalBand { get; set; } = "";
    public string AwayGoalBand { get; set; } = "";
    public string TotalGoalBand { get; set; } = "";
    public string FixtureStatus { get; set; } = "NS";
    public string Source { get; set; } = "Demo";
}

record Probabilities(int Home, int Draw, int Away);
record GoalBands(string Home, string Away, string Total);
record Match(string Home, string Away, string Date, string Round, string HomeForm, string AwayForm, string HomeGoals, string AwayGoals, Probabilities Probabilities, GoalBands Bands, int Id = 0, string? HomeCrest = null, string? AwayCrest = null, int? HomeScore = null, int? AwayScore = null);
record RecentMatch(string Date, string Opponent, int GoalsFor, int GoalsAgainst, bool AtHome);
record MatchDetails(Match Match, IReadOnlyList<RecentMatch> HomeRecent, IReadOnlyList<RecentMatch> AwayRecent);
static class DemoData
{
    public static readonly MatchRecord[] Records = [
        Create(1, "Inter", "Milan", 18, 18, "Giornata 7", "V V P V V", "V P V V P", 2.2m, 1.8m, 48, 27, 25, "1–3", "0–2", "2–4"),
        Create(2, "Roma", "Napoli", 19, 13, "Giornata 7", "V P V P V", "V V V P V", 1.6m, 2.0m, 31, 29, 40, "0–2", "1–3", "2–4"),
        Create(3, "Juventus", "Como", 19, 16, "Giornata 7", "V V V P V", "P V V P V", 1.8m, 1.4m, 54, 26, 20, "1–3", "0–2", "2–4"),
        Create(4, "Atalanta", "Lazio", 20, 18, "Giornata 7", "V P V V P", "V V P V P", 1.9m, 1.5m, 42, 30, 28, "1–3", "0–2", "2–4")
    ];
    public static readonly Match[] Matches = [
        new("Inter", "Milan", "Dom 18 ott · 20:45", "Giornata 7", "V V P V V", "V P V V P", "2.2", "1.8", new(48, 27, 25), new("1–3", "0–2", "2–4")),
        new("Roma", "Napoli", "Lun 19 ott · 15:00", "Giornata 7", "V P V P V", "V V V P V", "1.6", "2.0", new(31, 29, 40), new("0–2", "1–3", "2–4")),
        new("Juventus", "Como", "Lun 19 ott · 18:00", "Giornata 7", "V V V P V", "P V V P V", "1.8", "1.4", new(54, 26, 20), new("1–3", "0–2", "2–4")),
        new("Atalanta", "Lazio", "Mar 20 ott · 20:45", "Giornata 7", "V P V V P", "V V P V P", "1.9", "1.5", new(42, 30, 28), new("1–3", "0–2", "2–4"))
    ];

    private static MatchRecord Create(int id, string home, string away, int day, int hour, string round, string homeForm, string awayForm, decimal homeGoals, decimal awayGoals, int pHome, int pDraw, int pAway, string bandHome, string bandAway, string bandTotal) => new()
    {
        Id = id, KickoffUtc = new DateTime(2026, 10, day, hour - 2, 45, 0, DateTimeKind.Utc), HomeTeam = home, AwayTeam = away,
        Round = round, HomeForm = homeForm, AwayForm = awayForm, HomeGoalsAverage = homeGoals, AwayGoalsAverage = awayGoals,
        HomeWinProbability = pHome, DrawProbability = pDraw, AwayWinProbability = pAway, HomeGoalBand = bandHome,
        AwayGoalBand = bandAway, TotalGoalBand = bandTotal, Source = "Demo"
    };
}

