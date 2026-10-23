[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [string]$Destination = (Join-Path $PSScriptRoot '..\KClevoPanel-Windows-x64'))
$ErrorActionPreference = 'Stop'
& $Dotnet publish (Join-Path $PSScriptRoot 'App\KClevoPanel.App.csproj') -c Release -r win-x64 -p:Platform=x64 -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $Dotnet publish (Join-Path $PSScriptRoot 'TelemetryWorker\KClevoPanel.TelemetryWorker.csproj') -c Release -r win-x64 -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Worker build failed' }
& $Dotnet publish (Join-Path $PSScriptRoot 'PresetWorker\KClevoPanel.PresetWorker.csproj') -c Release -r win-x64 -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Direct preset worker build failed' }
& $Dotnet publish (Join-Path $PSScriptRoot 'FanWorker\KClevoPanel.FanWorker.csproj') -c Release -r win-x64 -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Fan worker build failed' }
Write-Output "Ready: $Destination\KClevoPanel.exe"
