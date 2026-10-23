[CmdletBinding()]
param([Parameter(Mandatory)][string]$Msi)
$ErrorActionPreference='Stop'
# Only disposable CI runners: never launch the app or send firmware commands.
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Install/uninstall verification requires a disposable GitHub Actions runner'}
$taskInstalled=Join-Path $env:LOCALAPPDATA 'Programs\KClevoPanel\KClevoPanel.exe'
$taskInstaller=Start-Process msiexec.exe -ArgumentList @('/i',('"'+[IO.Path]::GetFullPath($Msi)+'"'),'/qn','/norestart') -PassThru -Wait -WindowStyle Hidden
if($taskInstaller.ExitCode -notin @(0,3010)){throw "MSI install failed: $($taskInstaller.ExitCode)"}
try { if(!(Test-Path $taskInstalled)){throw 'Installed executable was not found'} }
finally {
 $taskUninstall=Start-Process msiexec.exe -ArgumentList @('/x',('"'+[IO.Path]::GetFullPath($Msi)+'"'),'/qn','/norestart') -PassThru -Wait -WindowStyle Hidden
 if($taskUninstall.ExitCode -notin @(0,3010)){throw "MSI uninstall failed: $($taskUninstall.ExitCode)"}
}
if(Test-Path $taskInstalled){throw 'Uninstall left the executable behind'}
