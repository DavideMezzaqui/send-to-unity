# Rimuove SendToUnity.
$ErrorActionPreference = 'SilentlyContinue'
[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\*\shell\SendToUnity', $false)
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('SendTo')) 'Progetto Unity.lnk') -Force
Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'SendToUnity') -Recurse -Force
Remove-Item -LiteralPath (Join-Path $env:TEMP 'SendToUnity-queue') -Recurse -Force
Write-Host 'Disinstallato.'
