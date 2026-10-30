param(
    [string]$OutputPath = (Join-Path $env:TEMP 'bryant-thermo-dashboard-export')
)

$ErrorActionPreference = 'Stop'

$podman = (Get-Command podman -ErrorAction SilentlyContinue).Source
if (-not $podman) {
    $podman = Join-Path $env:LOCALAPPDATA 'Programs\Podman\podman.exe'
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

# Stopping the containers lets Prometheus flush its WAL and Grafana close its database.
& $podman stop dashboard-grafana-1 dashboard-prometheus-1
if ($LASTEXITCODE -ne 0) {
    throw 'Could not stop the dashboard containers.'
}

foreach ($export in @(
        @{ Container = 'dashboard-prometheus-1'; Destination = '/prometheus'; File = 'prometheus-data.tar' },
        @{ Container = 'dashboard-grafana-1'; Destination = '/var/lib/grafana'; File = 'grafana-data.tar' })) {
    $volume = & $podman inspect $export.Container --format "{{range .Mounts}}{{if eq .Destination `"$($export.Destination)`"}}{{.Name}}{{end}}{{end}}"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($volume)) {
        throw "Could not find the data volume of container '$($export.Container)'."
    }
    $tarPath = Join-Path $OutputPath $export.File
    Remove-Item $tarPath -ErrorAction SilentlyContinue
    & $podman volume export $volume.Trim() --output $tarPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not export volume '$volume'."
    }
    Write-Host "Exported $volume to $tarPath"
}

Write-Host "Run an elevated install-dashboard-services.ps1 -ImportPath '$OutputPath' next."
