[CmdletBinding()]
param([string]$Dotnet='dotnet', [string]$PublishDirectory=(Join-Path $PSScriptRoot '..\artifacts\app'), [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\artifacts\release'), [switch]$SkipBuild)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$PublishDirectory=[IO.Path]::GetFullPath($PublishDirectory)
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
Push-Location $taskRoot
try {
    $taskVersion=([xml](Get-Content App\KClevoPanel.App.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if($taskVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Expected three-part release version'}
    if(!$SkipBuild){ & .\build.ps1 -Dotnet $Dotnet -Destination $PublishDirectory }
    foreach($taskExe in @('KClevoPanel.exe','KClevoPanel.TelemetryWorker.exe','KClevoPanel.PresetWorker.exe','KClevoPanel.FanWorker.exe')){if(!(Test-Path (Join-Path $PublishDirectory $taskExe))){throw "Missing $taskExe"}}
    Copy-Item README.md $PublishDirectory
    $taskDocs=Join-Path $PublishDirectory 'docs'
    New-Item -ItemType Directory $taskDocs -Force|Out-Null
    Copy-Item (Join-Path $taskRoot 'docs\*') $taskDocs -Recurse -Force
    New-Item -ItemType Directory $OutputDirectory -Force|Out-Null
    $taskWxs=Join-Path $taskRoot 'artifacts\installer\Package.wxs'
    & python packaging\generate-msi.py $PublishDirectory $taskWxs $taskVersion
    if($LASTEXITCODE -ne 0){throw 'MSI source generation failed'}
    & $Dotnet tool restore
    if($LASTEXITCODE -ne 0){throw 'WiX restore failed'}
    & $Dotnet tool run wix -- build $taskWxs -arch x64 -o (Join-Path $OutputDirectory "KClevoPanel-$taskVersion-x64.msi")
    if($LASTEXITCODE -ne 0){throw 'WiX build failed'}
    Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath (Join-Path $OutputDirectory "KClevoPanel-$taskVersion-Windows-x64.zip") -Force
    Get-ChildItem $OutputDirectory -File | Where-Object Extension -in '.msi','.zip' | Sort-Object Name | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.Name } | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Release artifacts: $OutputDirectory"
}
finally { Pop-Location }
