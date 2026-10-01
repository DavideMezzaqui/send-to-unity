$ErrorActionPreference = 'SilentlyContinue'
[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\*\shell\SendToUnity')
Remove-Item (Join-Path ([Environment]::GetFolderPath('SendTo')) 'Progetto Unity.lnk') -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'SendToUnity') -Recurse -Force
Write-Host 'Disinstallato.'
