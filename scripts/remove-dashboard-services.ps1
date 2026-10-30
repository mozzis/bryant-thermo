#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$serviceNames = 'BryantThermoGrafana', 'BryantThermoPrometheus'

foreach ($name in $serviceNames) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $service) { continue }
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $name -Force
        (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    & sc.exe delete $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not remove service '$name'." }
}

Get-NetFirewallRule -DisplayName 'Bryant Thermo Grafana' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

Write-Host 'Dashboard services and the Grafana firewall rule were removed.'
Write-Host "Binaries (Program Files\BryantThermo\Dashboard) and history (ProgramData\BryantThermo\Dashboard) were left in place."
