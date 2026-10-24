using System.Text.Json;
using System.Text.Json.Serialization;
using BryantThermo.Api.Carrier;
using BryantThermo.Api.Models;
using BryantThermo.Api.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

builder.Services.Configure<CarrierOptions>(builder.Configuration.GetSection(CarrierOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ThermostatState>();
builder.Services.AddHttpClient<CarrierCloudClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("BryantThermo/1.0");
})
// The client lives inside a singleton, so let the socket handler recycle connections (DNS changes)
// instead of relying on IHttpClientFactory handler rotation.
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
.SetHandlerLifetime(Timeout.InfiniteTimeSpan);
// CarrierCloudClient holds the auth token, so the poller must keep one instance for its lifetime.
builder.Services.AddSingleton<ThermostatPoller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ThermostatPoller>());

// OpenAPI document at /openapi/v1.json (XML doc comments on the models become schema descriptions).
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Bryant Thermostat API";
    document.Info.Version = "v1";
    document.Info.Description =
        "Read-only access to a Bryant Evolution Connex thermostat, via Carrier's cloud. Data is polled in the " +
        "background (default every 60 s); every response reflects the most recent poll. Temperatures are in " +
        "the unit the thermostat is set to (`temperatureUnit`).";
    return Task.CompletedTask;
}));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (allowedOrigins.Contains("*")) p.AllowAnyOrigin();
    else p.WithOrigins(allowedOrigins);
    p.WithMethods("GET", "POST").AllowAnyHeader();
}));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(); // wwwroot/index.html: demo page; wwwroot/docs/index.html: Swagger UI
app.MapOpenApi();     // GET /openapi/v1.json

var api = app.MapGroup("/api").WithTags("Thermostat");

// The API serves the last snapshot polled from Carrier's cloud. Until the first poll succeeds every data
// endpoint returns 503 with the reason (e.g. bad credentials) in the problem details.
api.MapGet("/thermostat/summary", (ThermostatState state) =>
        state.Snapshot is { } s ? Results.Ok(ThermostatSummary.From(s)) : NotReady(state))
    .WithName("GetSummary")
    .WithSummary("Dashboard summary")
    .WithDescription("Indoor temperature and humidity, heating and cooling set points, whether the system is " +
                     "heating, cooling or idle, and the humidifier set point — " +
                     "for the first (or only) zone.")
    .Produces<ThermostatSummary>()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/metrics", (ThermostatState state) =>
        Results.Text(PrometheusMetrics.Render(state.Snapshot), "text/plain; version=0.0.4; charset=utf-8"))
    .WithName("PrometheusMetrics")
    .WithSummary("Prometheus metrics")
    .WithDescription("Current first-zone temperature and humidity for Prometheus scraping.");

api.MapGet("/thermostat", (ThermostatState state) =>
        state.Snapshot is { } s ? Results.Ok(s) : NotReady(state))
    .WithName("GetThermostat")
    .WithSummary("Full snapshot")
    .WithDescription("System information, humidifier settings and every enabled zone.")
    .Produces<ThermostatSnapshot>()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/thermostat/humidity", (ThermostatState state) =>
        state.Snapshot is { } s ? Results.Ok(s.Humidity) : NotReady(state))
    .WithName("GetHumidity")
    .WithSummary("Humidifier settings")
    .WithDescription("Humidifier state and set points: the comfort profile currently in effect (`active`) " +
                     "plus the separate home, away and vacation profiles.")
    .Produces<HumiditySettings>()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/thermostat/zones", (ThermostatState state) =>
        state.Snapshot is { } s ? Results.Ok(s.Zones) : NotReady(state))
    .WithName("GetZones")
    .WithSummary("All zones")
    .WithDescription("Readings and settings for every enabled zone.")
    .Produces<IReadOnlyList<ZoneStatus>>()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapGet("/thermostat/zones/{id}", (string id, ThermostatState state) =>
    {
        if (state.Snapshot is not { } s) return NotReady(state);
        var zone = s.Zones.FirstOrDefault(z => string.Equals(z.Id, id, StringComparison.OrdinalIgnoreCase));
        return zone is null
            ? Results.Problem($"Zone '{id}' not found.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(zone);
    })
    .WithName("GetZone")
    .WithSummary("One zone")
    .WithDescription("Readings and settings for a single zone, by its Carrier zone id (\"1\" on a single-zone system).")
    .Produces<ZoneStatus>()
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

api.MapPost("/thermostat/refresh", (ThermostatPoller poller) =>
        poller.RequestRefresh()
            ? Results.Accepted("/api/thermostat")
            : Results.Problem("Refreshed too recently; try again in a few seconds.",
                statusCode: StatusCodes.Status429TooManyRequests))
    .WithName("RefreshThermostat")
    .WithSummary("Poll Carrier now")
    .WithDescription("Asks the background poller to fetch fresh data immediately instead of waiting for the next " +
                     "scheduled poll. The new data appears on subsequent GETs a few seconds later. " +
                     "Limited to once every 15 seconds.")
    .Produces(StatusCodes.Status202Accepted)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

app.MapGet("/api/health", (ThermostatState state, IOptions<CarrierOptions> opt, TimeProvider time) =>
    {
        var age = state.Snapshot is { } s ? time.GetUtcNow() - s.FetchedAtUtc : (TimeSpan?)null;
        var stale = age is null || age > TimeSpan.FromSeconds(opt.Value.StaleAfterSeconds);
        return Results.Ok(new HealthResponse
        {
            Status = stale ? "degraded" : "ok",
            LastFetchUtc = state.Snapshot?.FetchedAtUtc,
            AgeSeconds = age is { } a ? Math.Round(a.TotalSeconds) : null,
            ThermostatOnline = state.Snapshot?.Online,
            LastError = state.LastError,
            LastErrorAtUtc = state.LastErrorAtUtc,
        });
    })
    .WithTags("Health")
    .WithName("Health")
    .WithSummary("Backend health")
    .WithDescription("Whether fresh thermostat data is available, how old it is, and the last polling error.")
    .Produces<HealthResponse>();

app.Run();

static IResult NotReady(ThermostatState state) =>
    Results.Problem(
        state.LastError is { } err ? $"No thermostat data yet. Last error: {err}" : "No thermostat data yet; first poll in progress.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

public partial class Program;
