# bryant-thermo

A small ASP.NET Core (.NET 10) backend that reads a Bryant Evolution Connex thermostat
(SYSTXBBECW01-A) and serves its state to a web front end over a JSON REST API.

## How it gets the data (important)

The Evolution Connex does **not** expose a local API on your LAN. Its IP address (192.168.68.174)
can be pinged, but nothing on the thermostat answers HTTP requests for status. The thermostat
reports to Carrier's cloud, and the Bryant Home app reads it from there.

This backend does the same thing the app does: it signs in to Carrier's cloud with your
**Bryant Home app account** and queries the same (unofficial, undocumented) GraphQL API. Request
shapes follow the open-source [carrier_api](https://github.com/dahlb/carrier_api) project, which
supports both Carrier Infinity and Bryant Evolution systems. Because the API is private, Carrier can
change it without notice; endpoints and the OAuth client id are configurable in `appsettings.json`
so a change can usually be absorbed without a rebuild.

It polls once a minute in the background and serves front-end requests from memory, so the
front end can call it as often as it likes without hitting Carrier.

## Setup

1. Install the .NET 10 SDK.
2. Store your Bryant Home credentials as user secrets (kept out of the repo):

   ```powershell
   cd src\BryantThermo.Api
   dotnet user-secrets set "Carrier:Username" "you@example.com"
   dotnet user-secrets set "Carrier:Password" "your-password"
   ```

   Or use environment variables `Carrier__Username` / `Carrier__Password`
   (user secrets only load when `ASPNETCORE_ENVIRONMENT=Development`; use env vars in production).
3. Run it:

   ```powershell
   dotnet run --project src\BryantThermo.Api
   ```

   It listens on http://localhost:5080. Open that URL to see a small demo page that uses the API.
4. Run the tests: `dotnet test`

### Run automatically on Windows

The API can run as a Windows Service and start automatically at boot. Add machine-level environment variables named `Carrier__Username` and `Carrier__Password` in **System Properties > Advanced > Environment Variables > System variables**. These values are stored by Windows in the registry and are not encrypted, so protect access to the machine. Then open PowerShell as Administrator from the repository root and run:

```powershell
.\scripts\install-windows-service.ps1
```

The installer publishes the API, copies the machine-level Carrier variables into the service environment, and registers automatic startup and restart-on-failure. The service runs as LocalSystem in Production, so it does not depend on a user profile or Development user secrets. Verify it at `http://localhost:5080/api/health` or with `Get-Service BryantThermo`.

If more than one system is on your account, set `Carrier:SystemSerial` to pick one
(the serial appears in `GET /api/thermostat`).

## API

The API documents itself:

- **http://localhost:5080/docs**: interactive Swagger UI with every endpoint, field description and
  example response, plus "Try it out" buttons. The UI's scripts load from a CDN, so this page needs
  internet access; the API itself doesn't.
- **http://localhost:5080/openapi/v1.json**: the raw OpenAPI 3.1 document, for generating a
  typed client for your front end (e.g. `npx openapi-typescript http://localhost:5080/openapi/v1.json -o api.d.ts`).

Field descriptions come from the XML doc comments on the models in `Models/`, so editing a comment
updates the docs on the next build.

All responses are JSON. Temperatures are in the unit the thermostat is set to (`temperatureUnit`: `"F"` or `"C"`).

| Method | Path | Returns |
| --- | --- | --- |
| GET | `/api/thermostat/summary` | The fields you asked for, for the first zone: temperature, humidity, heat/cool set points, equipment action |
| GET | `/api/thermostat` | Full snapshot: system info plus every enabled zone |
| GET | `/api/thermostat/humidity` | Humidifier settings: the profile in effect plus home / away / vacation |
| GET | `/api/thermostat/zones` | All enabled zones |
| GET | `/api/thermostat/zones/{id}` | One zone (404 if unknown) |
| POST | `/api/thermostat/refresh` | Poll Carrier now (202), or 429 if polled in the last 15 s |
| GET | `/api/health` | `ok` / `degraded`, data age, last error |

Until the first successful poll, data endpoints return **503** with the reason
(e.g. `Carrier login failed: bad credentials`).

Example `GET /api/thermostat/summary`:

```json
{
  "systemSerial": "1234W567890",
  "online": true,
  "temperatureUnit": "F",
  "temperature": 75.5,
  "humidity": 52,
  "heatSetPoint": 68,
  "coolSetPoint": 74,
  "action": "cooling",
  "actionActive": true,
  "humidityTarget": 40,
  "humidifierEnabled": true,
  "humidifierRunning": false,
  "fetchedAtUtc": "2026-10-24T14:47:22.25Z"
}
```

Example `GET /api/thermostat/humidity` (also included as `humidity` in `GET /api/thermostat`):

```json
{
  "humidifierInstalled": true,
  "humidifierRunning": false,
  "humidifierLevel": 0,
  "activeProfile": "home",
  "active": { "mode": "manual", "humidifierEnabled": true, "heatingTargetPercent": 40,
              "coolingTargetPercent": 55, "overCool": true },
  "home":     { "...": "same shape" },
  "away":     { "...": "same shape" },
  "vacation": { "...": "same shape" }
}
```

### Field notes

- `action` is `heating`, `cooling`, `idle` or `unknown`, from Carrier's zone conditioning value.
  `actionActive` is false while the equipment is staging up (Carrier's `prep_*` / `pending_*`);
  the raw value is in the zone's `conditioning` field.
- The full snapshot includes Carrier's raw `indoorUnitOperationalStatus` and
  `outdoorUnitOperationalStatus` (`idu.opstat` and `odu.opstat`) so unit-level status can be
  compared with zone `conditioning` when diagnosing discrepancies.
- `fanMode` is the configured fan *setting* (`off` = auto, `low`, `med`, `high`), not whether it is spinning.
- **Humidity setting.** The humidifier setpoint is `heatingTargetPercent`, the relative humidity the
  system humidifies to while heating. Carrier stores it in 5 % steps (`rhtg` 8 = 40 %); the backend
  converts it to a percentage. Carrier keeps separate home, away and vacation humidity settings.
  `activeProfile` picks one from the current activity: `away` → away, `vacation` → vacation, anything
  else (home, sleep, wake, manual) → home. `active` holds that profile's values. In `/summary`,
  `humidityTarget` is that setpoint, or `null` when humidifying is turned off in the active profile.
- `coolingTargetPercent` and `overCool` are the dehumidify-while-cooling settings, included for completeness.
- `humidifierRunning` is the live on/off state, when the system reports it. `humidifierLevel` is
  Carrier's raw `humlvl` value, passed through unconverted.
- Data is only as fresh as what the thermostat last sent Carrier — compare `thermostatTimeUtc`
  and `fetchedAtUtc`.

## Configuration (`appsettings.json`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Carrier:PollIntervalSeconds` | 60 | Background poll interval (minimum 30) |
| `Carrier:StaleAfterSeconds` | 300 | Age after which `/api/health` reports `degraded` |
| `Carrier:SystemSerial` | (first) | Which system on the account to report |
| `Cors:AllowedOrigins` | localhost:3000, :5173 | Origins your front end is served from (`"*"` for any) |
| `Urls` | `http://0.0.0.0:5080` | Listen address (0.0.0.0 makes it reachable from other machines on your LAN) |

After a login failure the poller waits 15 minutes before retrying, so a wrong password
doesn't lock your Bryant account.

## Layout

```
src/BryantThermo.Api/
  Program.cs                      endpoints, DI, CORS
  Carrier/CarrierCloudClient.cs   login, token refresh, GraphQL query
  Carrier/CarrierStatusMapper.cs  Carrier JSON -> ThermostatSnapshot
  Services/ThermostatPoller.cs    background polling + in-memory state
  Models/ThermostatSnapshot.cs    response models (full snapshot, zones, humidity)
  Models/ApiResponses.cs          summary and health response models
  wwwroot/index.html              demo page
  wwwroot/docs/index.html         Swagger UI for the OpenAPI document
tests/BryantThermo.Tests/         xUnit tests for the mapping
```
