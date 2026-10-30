# Thermostat history dashboard

Grafana 12.3.0 with Business Charts 7.2.5 plots the first enabled zone's temperature and humidity in two stacked ECharts panels (temperature on top, humidity below). Each y-axis auto-scales with a 0.5 minimum tick interval. Prometheus scrapes the API every 15 seconds and retains history for five years; since the thermostat API polls Carrier every 60 seconds by default, repeated scrapes between API polls contain the same reading.

## Run as Windows services (recommended)

Prometheus and Grafana run as automatic Windows services next to the API (`BryantThermo`), so collection and the dashboard come back after a reboot without anyone signing in. There is no VM, container runtime or port proxy involved.

From an elevated PowerShell prompt in the repository root:

```powershell
.\scripts\install-dashboard-services.ps1
```

The script downloads Prometheus, Grafana and the WinSW service wrapper (each verified against a pinned SHA-256), installs the Business Charts plugin, and registers `BryantThermoPrometheus` and `BryantThermoGrafana` with restart-on-failure. Binaries and configs go to `C:\Program Files\BryantThermo\Dashboard`; history, the Grafana database and logs go to `C:\ProgramData\BryantThermo\Dashboard`. The services run as `LocalService`. Prometheus listens on `127.0.0.1:9090` and scrapes `localhost:5080`; Grafana listens on port 3000 and the script adds an inbound Private-profile firewall rule for it. Re-running the script reinstalls the services and applies updated files from `dashboard/` while keeping existing data.

To bring over history and Grafana users from a previous Podman setup, run this (not elevated) first, then pass its output folder to the installer:

```powershell
.\scripts\export-podman-dashboard-data.ps1
.\scripts\install-dashboard-services.ps1 -ImportPath $env:TEMP\bryant-thermo-dashboard-export
```

The installer also removes any Grafana port proxy on port 3000 and the `BryantThermoDashboard` Podman logon task, which would otherwise compete for the same ports. Remove the services with `.\scripts\remove-dashboard-services.ps1`. Service logs are in `C:\ProgramData\BryantThermo\Dashboard\logs` and `...\grafana\logs`.

## Run with Podman (alternative)

Use this instead of the services above, not alongside them; both want ports 3000 and 9090. It only runs while your Podman machine is up, which requires a signed-in user, and it does not restart by itself after a reboot.

1. Start the API on the host with its usual credentials:

   ```powershell
   dotnet run --project ..\src\BryantThermo.Api
   ```

2. Install a Compose provider. In Podman Desktop, open **Settings > Resources > Compose** and click **Setup**. Verify it is available:

   ```powershell
   podman compose version
   ```

3. Start the Podman machine:

   ```powershell
   podman machine list
   podman machine start
   ```

   If `podman machine list` shows no machine, create one first with `podman machine init`, then run `podman machine start`.

   On Windows, the Compose error's `npipe:////./pipe/podman-machine-default` and `\\.\pipe\podman-machine-default` shown by `podman machine inspect` are two notations for the same named pipe; the slash difference is expected. With the WSL provider, Podman exposes this Windows pipe for Windows Compose clients. If Compose cannot open the pipe even though the machine is running, restart the machine and check the output of `podman --log-level=debug machine start`:

   ```powershell
   podman machine stop
   podman --log-level=debug machine start
   ```

   If startup reports `API forwarding for Docker API clients is not available` or `win-sshproxy.exe failed to start`, the Windows-side API forwarder failed; Compose cannot connect until that is resolved. Check Windows Event Viewer for details about `win-sshproxy.exe`. This is separate from the Compose provider and dashboard configuration. Do not use `Get-ChildItem '\\.\pipe\'` to verify the pipe; PowerShell does not reliably enumerate named pipes.

4. From the repository root, start Grafana and Prometheus:

   ```powershell
   cd dashboard
   podman compose up -d
   ```

   If this fails with `failed to connect to the docker API at npipe:////./pipe/podman-machine-default`, check the Podman machine startup output for the API-forwarder failure described above. The slash formatting is not the cause. Open [http://localhost:3000](http://localhost:3000) and sign in with `admin` / `admin`. Grafana asks you to change the password on first sign-in.

### Access from other computers

With the Windows services, Grafana already listens on all interfaces; open `http://<this-computer's-LAN-IP>:3000` from the Private network. The rest of this section applies to the Podman setup.

Grafana is published on port 3000; Prometheus remains bound to localhost. On Windows with the WSL Podman machine, the host port forwarder may listen only on loopback. From another device on the same network, open `http://<this-computer's-LAN-IP>:3000` after enabling LAN access for the Podman machine. Use a strong Grafana password and allow inbound TCP port 3000 through Windows Firewall on the Private profile.

Prometheus is available at [http://localhost:9090](http://localhost:9090). The API metrics endpoint is `http://localhost:5080/api/metrics`.

Temperature samples are labeled `off`, `heating`, or `cooling`. Humidity samples carry a `state` label and a `humidifier` label (`running`, `off`, or `unknown` when Carrier doesn't report `humidifierRunning`); the humidity line is dashed while the humidifier is `off` and solid otherwise. Humidity is labeled `Low` below five percentage points under the active setpoint, `Normal` within ±5 percentage points of it, and `High` above five points over it. Low is purple, Normal is green, and High is yellow. If humidification is disabled or the target is missing, the gray `Setpoint unavailable` series is shown instead. If the API is not reachable, confirm it listens on host port 5080 before restarting the containers.