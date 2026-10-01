# Installa SendToUnity per l'utente corrente (non servono permessi di amministratore).
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dst = Join-Path $env:LOCALAPPDATA 'SendToUnity'
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item (Join-Path $src 'SendToUnity.ps1'), (Join-Path $src 'launcher.vbs') -Destination $dst -Force

$launcher = Join-Path $dst 'launcher.vbs'
$label = 'Invia al progetto Unity...'

# Voce del tasto destro su qualsiasi file
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\*\shell\SendToUnity')
$key.SetValue('', $label)
$cmd = $key.CreateSubKey('command')
$cmd.SetValue('', "wscript.exe `"$launcher`" `"%1`"")
$cmd.Close(); $key.Close()

# Scorciatoia in "Invia a": passa tutti i file in una sola volta (utile oltre i 15 file)
$sendTo = [Environment]::GetFolderPath('SendTo')
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $sendTo 'Progetto Unity.lnk'))
$lnk.TargetPath = 'wscript.exe'
$lnk.Arguments = "`"$launcher`""
$lnk.Save()

Write-Host 'Installato. Su Windows 11 la voce e'' sotto "Mostra altre opzioni".'
