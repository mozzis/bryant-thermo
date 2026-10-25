# Thermostat history dashboard

Grafana 12.3.0 with Business Charts 7.2.5 plots the first enabled zone's temperature and humidity in two stacked ECharts panels (temperature on top, humidity below). Each y-axis auto-scales with a 0.5 minimum tick interval. Prometheus scrapes the API every 15 seconds and retains history for five years; since the thermostat API polls Carrier every 60 seconds by default, repeated scrapes between API polls contain the same reading.

## Run

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

Grafana is published on port 3000; Prometheus remains bound to localhost. On Windows with the WSL Podman machine, the host port forwarder may listen only on loopback. From another device on the same network, open `http://<this-computer's-LAN-IP>:3000` after enabling LAN access for the Podman machine. Use a strong Grafana password and allow inbound TCP port 3000 through Windows Firewall on the Private profile.

Prometheus is available at [http://localhost:9090](http://localhost:9090). The API metrics endpoint is `http://localhost:5080/api/metrics`.

Temperature samples are labeled `off`, `heating`, or `cooling`. Humidity samples carry a `state` label and a `humidifier` label (`running`, `off`, or `unknown` when Carrier doesn't report `humidifierRunning`); the humidity line is dashed while the humidifier is `off` and solid otherwise. Humidity is labeled `Low` below five percentage points under the active setpoint, `Normal` within ±5 percentage points of it, and `High` above five points over it. Low is purple, Normal is green, and High is yellow. If humidification is disabled or the target is missing, the gray `Setpoint unavailable` series is shown instead. If the API is not reachable, confirm it listens on host port 5080 before restarting the containers.