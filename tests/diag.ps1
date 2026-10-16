# Diagnostica temporanea del menu dinamico (non fa parte del test)
$ErrorActionPreference = 'Continue'
function W([string]$t) { [Console]::WriteLine($t) }
$src = Split-Path -Parent $PSScriptRoot
$share = 'C:\stu-normale'
[void][IO.Directory]::CreateDirectory($share)
Copy-Item -Recurse -Force (Join-Path $src '*') $share
icacls $share /grant 'Users:(OI)(CI)M' /T /Q | Out-Null

$password = 'Pr0va!' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$secure = ConvertTo-SecureString $password -AsPlainText -Force
New-LocalUser -Name 'stuprova' -Password $secure -AccountNeverExpires | Out-Null
$cred = New-Object Management.Automation.PSCredential ('.\stuprova', $secure)

$probe = Join-Path $share 'probe.ps1'
[IO.File]::WriteAllText($probe, @'
$out = 'C:\stu-normale\probe.txt'
function Say([string]$t) { [IO.File]::AppendAllText($out, $t + "`r`n") }
try {
    Say ('utente: ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name + ', admin: ' + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    $p = Start-Process cmd.exe -ArgumentList '/c', 'C:\stu-normale\install.cmd', '/quiet' -Wait -PassThru -WindowStyle Hidden
    Say ('install.cmd: ' + $p.ExitCode)
    $r = try { [void][Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]'6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23')); 'creato' } catch { $_.Exception.InnerException.Message }
    Say ('comando: ' + $r)
    Add-Type -Path 'C:\stu-normale\tests\WinTest.cs' -ReferencedAssemblies System.Drawing, System.Windows.Forms
    $file = Join-Path $env:TEMP 'prova.png'
    [IO.File]::WriteAllText($file, 'x')
    $items = [ExplorerMenu]::Submenu(@($file), 'Invia a Unity', '')
    Say ('menu: ' + ($(if ($null -eq $items) { 'voce assente: ' + ([ExplorerMenu]::TopItems -join ' | ') } else { $items -join ' | ' })))
} catch { Say ('errore: ' + $_.Exception.ToString()) }
Say 'fine'
'@)
try {
    $p = Start-Process powershell.exe -Credential $cred -LoadUserProfile -WorkingDirectory $share -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probe -PassThru
    $p.WaitForExit(120000) | Out-Null
} catch { W "avvio come utente normale: $($_.Exception.Message)" }
if (Test-Path "$share\probe.txt") { foreach ($l in [IO.File]::ReadAllLines("$share\probe.txt")) { W $l } } else { W 'nessun risultato' }
Remove-LocalUser -Name 'stuprova'
exit 0
