#Requires -RunAsAdministrator

param(
    # Folder produced by export-podman-dashboard-data.ps1; its history is imported on first install.
    [string]$ImportPath
)

$ErrorActionPreference = 'Stop'

$prometheusVersion = '3.6.0'
$prometheusUrl = "https://github.com/prometheus/prometheus/releases/download/v$prometheusVersion/prometheus-$prometheusVersion.windows-amd64.zip"
$prometheusSha256 = '168d5ff010696737eb1c8c4075657361c7d3fba285f2e2a140a09ddbff2c1027'
$grafanaVersion = '12.3.0'
$grafanaUrl = "https://dl.grafana.com/oss/release/grafana-$grafanaVersion.windows-amd64.zip"
$grafanaSha256 = '79ea9c13c45af8f0ab91d214c1deb26dc5361885ab14dfc6fbc469438537078c'
$echartsPluginVersion = '7.2.5'
$winswUrl = 'https://github.com/winsw/winsw/releases/download/v2.12.0/WinSW-x64.exe'
$winswSha256 = '05b82d46ad331cc16bdc00de5c6332c1ef818df8ceefcd49c726553209b3a0da'

$apiPort = 5080
$grafanaPort = 3000
$prometheusPort = 9090

$repoDashboard = (Resolve-Path (Join-Path $PSScriptRoot '..\dashboard')).Path
$installRoot = Join-Path $env:ProgramFiles 'BryantThermo\Dashboard'
$dataRoot = Join-Path $env:ProgramData 'BryantThermo\Dashboard'
$downloadRoot = Join-Path $env:TEMP 'bryant-thermo-dashboard-downloads'

$prometheusHome = Join-Path $installRoot 'prometheus'
$grafanaHome = Join-Path $installRoot 'grafana'
$configRoot = Join-Path $installRoot 'config'
$servicesRoot = Join-Path $installRoot 'services'
$prometheusData = Join-Path $dataRoot 'prometheus' # holds the "data" TSDB folder, as in the container volume
$grafanaData = Join-Path $dataRoot 'grafana\data'
$grafanaPlugins = Join-Path $dataRoot 'grafana\plugins'
$grafanaLogs = Join-Path $dataRoot 'grafana\logs'
$serviceLogs = Join-Path $dataRoot 'logs'

$services = @(
    @{ Name = 'BryantThermoPrometheus'; Display = 'Bryant Thermostat Prometheus' },
    @{ Name = 'BryantThermoGrafana'; Display = 'Bryant Thermostat Grafana' }
)

function Get-VerifiedDownload([string]$Url, [string]$Sha256) {
    New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
    $path = Join-Path $downloadRoot ([IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
    if (-not (Test-Path $path) -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $Sha256.ToUpperInvariant()) {
        Write-Host "Downloading $Url"
        Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
    }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $Sha256.ToUpperInvariant()) {
        Remove-Item $path -Force
        throw "SHA-256 mismatch for $Url; the download was discarded."
    }
    return $path
}

function Expand-Release([string]$ZipPath, [string]$Destination) {
    $staging = Join-Path $downloadRoot ([IO.Path]::GetRandomFileName())
    Expand-Archive -Path $ZipPath -DestinationPath $staging
    $inner = Get-ChildItem $staging -Directory | Select-Object -First 1
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path $Destination) -Force | Out-Null
    Move-Item $inner.FullName $Destination
    Remove-Item $staging -Recurse -Force
}

function Remove-ExistingService([string]$Name) {
    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    (Get-Service -Name $Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    & sc.exe delete $Name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not remove existing service '$Name'." }
    Start-Sleep -Seconds 2
}

function New-WinswService([string]$Name, [string]$Display, [string]$Description, [string]$Executable, [string]$Arguments, [hashtable]$Environment) {
    $wrapper = Join-Path $servicesRoot "$Name.exe"
    Copy-Item $winswPath $wrapper -Force
    $envXml = ($Environment.GetEnumerator() | ForEach-Object {
        "  <env name=`"$($_.Key)`" value=`"$([Security.SecurityElement]::Escape($_.Value))`"/>"
    }) -join "`n"
    $xml = @"
<service>
  <id>$Name</id>
  <name>$Display</name>
  <description>$Description</description>
  <executable>$Executable</executable>
  <arguments>$([Security.SecurityElement]::Escape($Arguments))</arguments>
$envXml
  <startmode>Automatic</startmode>
  <logpath>$serviceLogs</logpath>
  <log mode="roll-by-size"><sizeThreshold>10240</sizeThreshold><keepFiles>5</keepFiles></log>
  <onfailure action="restart" delay="5 sec"/>
  <onfailure action="restart" delay="15 sec"/>
  <onfailure action="restart" delay="60 sec"/>
  <resetfailure>1 day</resetfailure>
</service>
"@
    Set-Content -Path (Join-Path $servicesRoot "$Name.xml") -Value $xml -Encoding UTF8
    & $wrapper install
    if ($LASTEXITCODE -ne 0) { throw "Could not install service '$Name'." }
    & sc.exe config $Name obj= 'NT AUTHORITY\LocalService' password= '""' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set the account for service '$Name'." }
}

$winswPath = Get-VerifiedDownload $winswUrl $winswSha256
$prometheusZip = Get-VerifiedDownload $prometheusUrl $prometheusSha256
$grafanaZip = Get-VerifiedDownload $grafanaUrl $grafanaSha256

foreach ($service in $services) { Remove-ExistingService $service.Name }

# A leftover Podman port forward or proxy would hold ports the services need.
& netsh interface portproxy show v4tov4 | Select-String "\s$grafanaPort\s" | ForEach-Object {
    $listenAddress = ($_.Line -split '\s+' | Where-Object { $_ })[0]
    & netsh interface portproxy delete v4tov4 listenaddress=$listenAddress listenport=$grafanaPort | Out-Null
    Write-Host "Removed port proxy $listenAddress`:$grafanaPort; Grafana now listens on the network directly."
}
$busy = netstat -ano -p tcp | Select-String 'LISTENING' | Select-String ":($grafanaPort|$prometheusPort)\s"
if ($busy) {
    throw "Port $grafanaPort or $prometheusPort is already in use. Stop the Podman dashboard containers first:`n$($busy -join "`n")"
}

New-Item -ItemType Directory -Force -Path $servicesRoot, $configRoot, $prometheusData, $grafanaData, $grafanaPlugins, $grafanaLogs, $serviceLogs | Out-Null
Expand-Release $prometheusZip $prometheusHome
Expand-Release $grafanaZip $grafanaHome
# Moved files keep the temp folder's ACLs; the services run as LocalService and need the inherited ones.
foreach ($releaseDir in $prometheusHome, $grafanaHome) {
    & icacls.exe $releaseDir /reset /T /C | Out-Null
}

if ($ImportPath) {
    $prometheusTar = Join-Path $ImportPath 'prometheus-data.tar'
    $grafanaTar = Join-Path $ImportPath 'grafana-data.tar'
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if ((Test-Path $prometheusTar) -and -not (Get-ChildItem $prometheusData -Force)) {
        & $tar -xf $prometheusTar -C $prometheusData
        if ($LASTEXITCODE -ne 0) { throw 'Could not import Prometheus history.' }
        Remove-Item (Join-Path $prometheusData 'data\lock') -ErrorAction SilentlyContinue
        Write-Host 'Imported Prometheus history.'
    }
    if ((Test-Path $grafanaTar) -and -not (Test-Path (Join-Path $grafanaData 'grafana.db'))) {
        & $tar -xf $grafanaTar -C $grafanaData grafana.db
        if ($LASTEXITCODE -ne 0) { throw 'Could not import the Grafana database.' }
        Write-Host 'Imported Grafana settings and users.'
    }
}

& icacls.exe $dataRoot /grant 'NT AUTHORITY\LocalService:(OI)(CI)M' /T /C | Out-Null

# Container paths and hostnames in the Compose config become local ones.
$prometheusConfig = (Get-Content (Join-Path $repoDashboard 'prometheus\prometheus.yml') -Raw).Replace('host.containers.internal:5080', "localhost:$apiPort")
Set-Content (Join-Path $configRoot 'prometheus.yml') $prometheusConfig -Encoding UTF8

$provisioning = Join-Path $configRoot 'grafana\provisioning'
$dashboards = Join-Path $installRoot 'dashboards'
Remove-Item $provisioning, $dashboards -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repoDashboard 'grafana\provisioning') $provisioning -Recurse
Copy-Item (Join-Path $repoDashboard 'grafana\dashboards') $dashboards -Recurse
$datasourceFile = Join-Path $provisioning 'datasources\prometheus.yml'
(Get-Content $datasourceFile -Raw).Replace('http://prometheus:9090', "http://127.0.0.1:$prometheusPort") | Set-Content $datasourceFile -Encoding UTF8
$providerFile = Join-Path $provisioning 'dashboards\provider.yml'
(Get-Content $providerFile -Raw).Replace('/var/lib/grafana/dashboards', ($dashboards -replace '\\', '/')) | Set-Content $providerFile -Encoding UTF8

$grafanaEnvironment = @{
    GF_PATHS_DATA         = $grafanaData
    GF_PATHS_LOGS         = $grafanaLogs
    GF_PATHS_PLUGINS      = $grafanaPlugins
    GF_PATHS_PROVISIONING = $provisioning
    GF_SERVER_HTTP_PORT   = "$grafanaPort"
}
foreach ($entry in $grafanaEnvironment.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
}
& (Join-Path $grafanaHome 'bin\grafana.exe') cli --homepath $grafanaHome --pluginsDir $grafanaPlugins plugins install volkovlabs-echarts-panel $echartsPluginVersion
if ($LASTEXITCODE -ne 0) { throw 'Could not install the Business Charts plugin.' }
& icacls.exe $dataRoot /grant 'NT AUTHORITY\LocalService:(OI)(CI)M' /T /C | Out-Null

New-WinswService `
    -Name 'BryantThermoPrometheus' `
    -Display 'Bryant Thermostat Prometheus' `
    -Description 'Scrapes the thermostat API every 15 seconds and keeps five years of history.' `
    -Executable (Join-Path $prometheusHome 'prometheus.exe') `
    -Arguments "--config.file=`"$(Join-Path $configRoot 'prometheus.yml')`" --storage.tsdb.path=`"$(Join-Path $prometheusData 'data')`" --storage.tsdb.retention.time=5y --web.listen-address=127.0.0.1:$prometheusPort" `
    -Environment @{}

New-WinswService `
    -Name 'BryantThermoGrafana' `
    -Display 'Bryant Thermostat Grafana' `
    -Description 'Serves the thermostat history dashboard.' `
    -Executable (Join-Path $grafanaHome 'bin\grafana.exe') `
    -Arguments "server --homepath `"$grafanaHome`"" `
    -Environment $grafanaEnvironment

$firewallRule = 'Bryant Thermo Grafana'
if (-not (Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Protocol TCP -LocalPort $grafanaPort -Profile Private -Action Allow | Out-Null
}

# The Podman logon task would restart the containers and fight over these ports.
if (Get-ScheduledTask -TaskName 'BryantThermoDashboard' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName 'BryantThermoDashboard' -Confirm:$false
    Write-Host "Removed the 'BryantThermoDashboard' Podman logon task."
}

foreach ($service in $services) {
    Start-Service -Name $service.Name
    (Get-Service -Name $service.Name).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
}

Write-Host 'Prometheus and Grafana are running as automatic Windows services.'
Write-Host "Grafana: http://localhost:$grafanaPort (Prometheus is bound to localhost:$prometheusPort). Logs: $serviceLogs and $grafanaLogs"
