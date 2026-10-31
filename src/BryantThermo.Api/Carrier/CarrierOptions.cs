namespace BryantThermo.Api.Carrier;

/// <summary>Settings bound from the "Carrier" configuration section.</summary>
public sealed class CarrierOptions
{
    public const string SectionName = "Carrier";

    /// <summary>The e-mail / user name you use in the Bryant Home (or Carrier Home) app.</summary>
    public string Username { get; set; } = "";

    /// <summary>The matching password. Keep it in user-secrets or an environment variable, not appsettings.json.</summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// Optional. Serial number of the system to report on. Leave empty to use the first system on the account.
    /// </summary>
    public string? SystemSerial { get; set; }

    /// <summary>How often to poll Carrier's cloud. Values below 30 s are raised to 30 s.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>A snapshot older than this is reported as stale by /api/health.</summary>
    public int StaleAfterSeconds { get; set; } = 300;

    // Endpoints and client id used by Carrier's own apps. Exposed so they can be changed without a rebuild
    // if Carrier moves them.
    public string GraphQlUrl { get; set; } = "https://dataservice.infinity.iot.carrier.com/graphql";
    public string LoginUrl { get; set; } = "https://dataservice.infinity.iot.carrier.com/graphql-no-auth";
    public string TokenUrl { get; set; } = "https://sso.carrier.com/oauth2/default/v1/token";
    public string OAuthClientId { get; set; } = "0oa1ce7hwjuZbfOMB4x7";

    /// <summary>
    /// Time zone of the thermostat (IANA or Windows id), used to find the next schedule period when holding.
    /// Empty uses this machine's local time zone.
    /// </summary>
    public string? TimeZone { get; set; }
}
