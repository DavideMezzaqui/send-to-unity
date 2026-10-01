# Installa SendToUnity per l'utente corrente (non servono permessi di amministratore).
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dst = Join-Path $env:LOCALAPPDATA 'SendToUnity'
[void][IO.Directory]::CreateDirectory($dst)
foreach ($f in 'SendToUnity.ps1', 'launcher.vbs') {
    Copy-Item -LiteralPath (Join-Path $src $f) -Destination $dst -Force
    # I file scaricati da internet sono "bloccati": wscript chiederebbe conferma a ogni uso
    Unblock-File -LiteralPath (Join-Path $dst $f)
}

$launcher = Join-Path $dst 'launcher.vbs'
$wscript  = Join-Path $env:WINDIR 'System32\wscript.exe'
$hubExe   = Join-Path $env:ProgramFiles 'Unity Hub\Unity Hub.exe'

# Voce del tasto destro su qualsiasi file
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\*\shell\SendToUnity')
$key.SetValue('', 'Invia al progetto Unity...')
if (Test-Path -LiteralPath $hubExe) { $key.SetValue('Icon', "`"$hubExe`",0") }
$cmd = $key.CreateSubKey('command')
$cmd.SetValue('', "`"$wscript`" `"$launcher`" `"%1`"")
$cmd.Close(); $key.Close()

# Scorciatoia in "Invia a": riceve tutti i file in una volta (serve oltre i 15 file)
$sendTo = [Environment]::GetFolderPath('SendTo')
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $sendTo 'Progetto Unity.lnk'))
$lnk.TargetPath = $wscript
$lnk.Arguments = "`"$launcher`""
if (Test-Path -LiteralPath $hubExe) { $lnk.IconLocation = "$hubExe,0" }
$lnk.Save()

Write-Host 'Installato.'
Write-Host 'Tasto destro sui file > "Invia al progetto Unity..." (su Windows 11 sotto "Mostra altre opzioni").'
Write-Host 'Oppure tasto destro > Invia a > Progetto Unity.'
