#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$serviceName = 'BryantThermo'
$projectPath = Join-Path $PSScriptRoot '..\src\BryantThermo.Api\BryantThermo.Api.csproj'
$installPath = Join-Path $env:ProgramFiles 'BryantThermo\Api'
$executablePath = Join-Path $installPath 'BryantThermo.Api.exe'
$carrierUsername = [Environment]::GetEnvironmentVariable('Carrier__Username', 'Machine')
$carrierPassword = [Environment]::GetEnvironmentVariable('Carrier__Password', 'Machine')

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service '$serviceName' already exists. Stop and remove it before reinstalling."
}

if ([string]::IsNullOrWhiteSpace($carrierUsername) -or [string]::IsNullOrWhiteSpace($carrierPassword)) {
    throw 'Set machine-level Carrier__Username and Carrier__Password environment variables before installing the service.'
}

New-Item -ItemType Directory -Path $installPath -Force | Out-Null
dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $installPath
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed; the Windows service was not installed.'
}

New-Service `
    -Name $serviceName `
    -DisplayName 'Bryant Thermostat API' `
    -Description 'Polls Carrier for thermostat status and serves the local API.' `
    -BinaryPathName "`"$executablePath`"" `
    -StartupType Automatic | Out-Null

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
New-ItemProperty `
    -Path $serviceRegistryPath `
    -Name Environment `
    -PropertyType MultiString `
    -Value @(
        'ASPNETCORE_ENVIRONMENT=Production',
        'DOTNET_ENVIRONMENT=Production',
        "Carrier__Username=$carrierUsername",
        "Carrier__Password=$carrierPassword"
    ) `
    -Force | Out-Null

& sc.exe failure $serviceName 'reset=' '86400' 'actions=' 'restart/5000/restart/15000/restart/60000' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Could not configure service recovery actions.'
}

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "Service '$serviceName' is running and configured to start automatically."
Write-Host 'Check http://localhost:5080/api/health to confirm the API is responding.'