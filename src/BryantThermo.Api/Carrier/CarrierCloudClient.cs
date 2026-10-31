using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace BryantThermo.Api.Carrier;

/// <summary>
/// Minimal client for the (unofficial, undocumented) GraphQL API behind the Bryant Home / Carrier Home apps.
/// The Evolution Connex thermostat has no local API, so this is the only supported way to read it.
/// Request shapes follow the open-source carrier_api project (github.com/dahlb/carrier_api).
/// </summary>
public sealed class CarrierCloudClient(HttpClient http, IOptions<CarrierOptions> options, ILogger<CarrierCloudClient> log)
{
    private readonly CarrierOptions _opt = options.Value;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private string? _accessToken;
    private string _tokenType = "Bearer";
    private string? _refreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    private const string LoginMutation = """
        mutation assistedLogin($input: AssistedLoginInput!) {
          assistedLogin(input: $input) {
            success
            status
            errorMessage
            data { token_type expires_in access_token scope refresh_token }
          }
        }
        """;

    // Only the fields this backend needs.
    private const string SystemsQuery = """
        query getInfinitySystems($userName: String!) {
          infinitySystems(userName: $userName) {
            profile { serial name model brand }
            status {
              utcTime
              isDisconnected
              cfgem
              mode
              oat
              idu { opstat cfm blwrpm }
              odu { opstat }
              humid
              humlvl
              zones { id rt rh fan htsp clsp hold enabled currentActivity zoneconditioning }
            }
            config {
              cfghumid
              humidityHome { humid humidifier rhtg rclg rclgovercool }
              humidityAway { humid humidifier rhtg rclg rclgovercool }
              humidityVacation { humid humidifier rhtg rclg rclgovercool }
              zones { id name enabled hold holdActivity otmr
                program { id day { id zoneId period { id zoneId dayId activity time enabled } } } }
            }
          }
        }
        """;

    /// <summary>Returns the raw <c>infinitySystems</c> array for the configured account.</summary>
    public async Task<JsonArray> GetSystemsAsync(CancellationToken ct)
    {
        var data = await AuthedQueryAsync("getInfinitySystems", SystemsQuery,
            new JsonObject { ["userName"] = _opt.Username }, ct);

        return data["infinitySystems"] as JsonArray
               ?? throw new CarrierApiException("Response did not contain infinitySystems.");
    }

    private const string ZoneActivityMutation = """
        mutation updateInfinityZoneActivity($input: InfinityZoneActivityInput!) {
          updateInfinityZoneActivity(input: $input) { etag }
        }
        """;

    private const string ZoneConfigMutation = """
        mutation updateInfinityZoneConfig($input: InfinityZoneConfigInput!) {
          updateInfinityZoneConfig(input: $input) { etag }
        }
        """;

    /// <summary>Sets the heat/cool set points of the zone's "manual" activity.</summary>
    public Task SetManualSetpointsAsync(string serial, string zoneId, double heat, double cool, string? fan, CancellationToken ct)
    {
        var input = new JsonObject
        {
            ["serial"] = serial,
            ["zoneId"] = zoneId,
            ["activityType"] = "manual",
            ["htsp"] = heat.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            ["clsp"] = cool.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(fan)) input["fan"] = fan;
        return AuthedQueryAsync("updateInfinityZoneActivity", ZoneActivityMutation, new JsonObject { ["input"] = input }, ct);
    }

    /// <summary>Holds the zone on its manual activity until <paramref name="until"/> ("HH:mm"), or indefinitely if null.</summary>
    public Task HoldManualAsync(string serial, string zoneId, string? until, CancellationToken ct) =>
        AuthedQueryAsync("updateInfinityZoneConfig", ZoneConfigMutation, new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["serial"] = serial,
                ["zoneId"] = zoneId,
                ["hold"] = "on",
                ["holdActivity"] = "manual",
                ["otmr"] = until,
            },
        }, ct);

    /// <summary>Clears any hold so the zone follows its program again.</summary>
    public Task ResumeScheduleAsync(string serial, string zoneId, CancellationToken ct) =>
        AuthedQueryAsync("updateInfinityZoneConfig", ZoneConfigMutation, new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["serial"] = serial,
                ["zoneId"] = zoneId,
                ["hold"] = "off",
                ["holdActivity"] = null,
                ["otmr"] = null,
            },
        }, ct);

    private async Task<JsonNode> AuthedQueryAsync(string operation, string query, JsonObject variables, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        try
        {
            return await PostGraphQlAsync(_opt.GraphQlUrl, operation, query, variables, authorize: true, ct);
        }
        catch (CarrierAuthException)
        {
            // Token rejected early (revoked, clock skew...). Log in from scratch once and retry.
            log.LogInformation("Carrier rejected the access token; logging in again.");
            await LoginAsync(ct);
            return await PostGraphQlAsync(_opt.GraphQlUrl, operation, query, variables, authorize: true, ct);
        }
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        // Renew a minute early so a token never expires mid-request.
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1)) return;

        await _authLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1)) return;

            if (_refreshToken is not null)
            {
                try
                {
                    await RefreshAsync(ct);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Token refresh failed; falling back to a full login.");
                }
            }
            await LoginCoreAsync(ct);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try { await LoginCoreAsync(ct); }
        finally { _authLock.Release(); }
    }

    private async Task LoginCoreAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.Username) || string.IsNullOrWhiteSpace(_opt.Password))
            throw new CarrierAuthException(
                "Carrier credentials are not configured. Set Carrier:Username and Carrier:Password (see README).");

        var variables = new JsonObject
        {
            ["input"] = new JsonObject { ["username"] = _opt.Username, ["password"] = _opt.Password },
        };
        var data = await PostGraphQlAsync(_opt.LoginUrl, "assistedLogin", LoginMutation, variables, authorize: false, ct);

        var result = data["assistedLogin"];
        if (result?["success"]?.GetValue<bool>() != true)
        {
            var msg = result?["errorMessage"]?.ToString() ?? result?["status"]?.ToString() ?? "unknown error";
            throw new CarrierAuthException($"Carrier login failed: {msg}");
        }

        StoreToken(result["data"]!);
        log.LogInformation("Logged in to Carrier cloud; token valid until {Expiry:u}.", _expiresAt);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _opt.OAuthClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken!,
            ["scope"] = "offline_access",
        });
        using var resp = await http.PostAsync(_opt.TokenUrl, form, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>(ct)
                   ?? throw new CarrierApiException("Empty token refresh response.");
        StoreToken(json);
        log.LogDebug("Refreshed Carrier token; valid until {Expiry:u}.", _expiresAt);
    }

    private void StoreToken(JsonNode tokenData)
    {
        _accessToken = tokenData["access_token"]?.ToString()
                       ?? throw new CarrierAuthException("Token response had no access_token.");
        _tokenType = tokenData["token_type"]?.ToString() ?? "Bearer";
        _refreshToken = tokenData["refresh_token"]?.ToString() ?? _refreshToken;
        var expiresIn = CarrierJson.GetDouble(tokenData["expires_in"]) ?? 3600;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
    }

    private async Task<JsonNode> PostGraphQlAsync(
        string url, string operation, string query, JsonObject variables, bool authorize, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // Buffered (not streamed) so the request carries a Content-Length instead of chunked encoding.
            Content = new StringContent(new JsonObject
            {
                ["operationName"] = operation,
                ["query"] = query,
                ["variables"] = variables,
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (authorize)
            req.Headers.Authorization = new AuthenticationHeaderValue(_tokenType, _accessToken);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CarrierAuthException($"Carrier returned {(int)resp.StatusCode} for {operation}.");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonNode>(ct)
                   ?? throw new CarrierApiException($"Empty response for {operation}.");

        if (body["errors"] is JsonArray { Count: > 0 } errors)
        {
            var messages = string.Join("; ", errors.Select(e => e?["message"]?.ToString()));
            if (messages.Contains("unauth", StringComparison.OrdinalIgnoreCase) ||
                messages.Contains("token", StringComparison.OrdinalIgnoreCase))
                throw new CarrierAuthException($"{operation}: {messages}");
            throw new CarrierApiException($"{operation}: {messages}");
        }

        return body["data"] ?? throw new CarrierApiException($"No data in response for {operation}.");
    }
}

public class CarrierApiException(string message) : Exception(message);

public sealed class CarrierAuthException(string message) : CarrierApiException(message);
