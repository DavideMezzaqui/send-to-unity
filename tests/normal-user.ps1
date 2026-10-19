# Parte del test su Windows (sezione 15): gira come utente normale, senza permessi di
# amministratore, e scrive l'esito in un file (OK/KO <controllo>, altre righe = note).
param([string]$report)

$ErrorActionPreference = 'Stop'
function Say([string]$text) { [IO.File]::AppendAllText($report, $text + "`r`n") }
function Check([bool]$ok, [string]$what) { if ($ok) { Say "OK $what" } else { Say "KO $what" } }

try {
    $src = Split-Path -Parent $PSScriptRoot
    $app = Join-Path $env:LOCALAPPDATA 'SendToUnity'
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Check (-not $admin) "gira come utente normale ($([Security.Principal.WindowsIdentity]::GetCurrent().Name))"

    # senza amministratore Windows chiederebbe la password per il menu ridotto: qui non c'e' nessuno
    $env:SENDTOUNITY_SOLO_MENU_CLASSICO = '1'
    $install = Start-Process cmd.exe -ArgumentList '/c', (Join-Path $src 'install.cmd'), '/quiet' -Wait -PassThru -WindowStyle Hidden
    Check ($install.ExitCode -eq 0) 'install.cmd senza permessi di amministratore'

    # un progetto in Unity Hub, con la cartella gia' scelta, aperto in un finto Unity
    $project = Join-Path $env:TEMP 'Progetto Normale'
    [void][IO.Directory]::CreateDirectory((Join-Path $project 'Assets'))
    [void][IO.Directory]::CreateDirectory((Join-Path $project 'ProjectSettings'))
    [void][IO.Directory]::CreateDirectory((Join-Path $project 'Temp'))
    $hub = Join-Path $env:APPDATA 'UnityHub'
    [void][IO.Directory]::CreateDirectory($hub)
    $u = $project.Replace('\', '/')
    [IO.File]::WriteAllText((Join-Path $hub 'projects-v1.json'),
        '{"schema_version":"v1","data":{"' + $u + '":{"title":"Normale","path":"' + $u + '","lastModified":1700000000000}}}')
    $json = '{"ExtraProjects":[],"Projects":[{"Path":"' + $project.Replace('\', '\\') + '","Folder":"Assets/Arrivati","LastUsed":1}]}'
    [IO.File]::WriteAllText((Join-Path $app 'settings.json'), $json)
    # Unity tiene aperto Temp\UnityLockfile finche' il progetto e' aperto
    $lock = [IO.File]::Open((Join-Path $project 'Temp\UnityLockfile'), 'OpenOrCreate', 'ReadWrite', 'None')

    Add-Type -Path (Join-Path $PSScriptRoot 'WinTest.cs') -ReferencedAssemblies System.Drawing, System.Windows.Forms
    $file = Join-Path $env:TEMP 'dal_menu.png'
    [IO.File]::WriteAllText($file, 'U')
    $items = [ExplorerMenu]::Submenu(@($file), 'Invia a Unity', '')
    Check ($null -ne $items) 'il menu del tasto destro contiene "Invia a Unity"'
    Say ('sottomenu: ' + (@($items) -join ' | '))
    $at = [Array]::FindIndex([string[]]@($items), [Predicate[string]]{ param($t) $t -like 'Progetto Normale*Assets/Arrivati' })
    Check ($at -ge 0) 'il progetto compare nel menu con la sua cartella'
    Check ($at -ge 0 -and [ExplorerMenu]::Icons[$at]) 'il progetto aperto ha il pallino verde'

    [void][ExplorerMenu]::Submenu(@($file), 'Invia a Unity', 'Progetto Normale')
    Check ([ExplorerMenu]::Invoked) 'clic sulla voce del progetto'
    $arrived = Join-Path $project 'Assets\Arrivati\dal_menu.png'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $arrived) -and $watch.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
    Check (Test-Path -LiteralPath $arrived) 'il file arriva nel progetto'
    $lock.Dispose()

    $log = Join-Path $app 'log.txt'
    if (Test-Path $log) { foreach ($line in [IO.File]::ReadAllLines($log)) { Say ('log: ' + $line) } }
    foreach ($p in [Diagnostics.Process]::GetProcessesByName('SendToUnity')) { try { $p.Kill() } catch { } }
    $uninstall = Start-Process cmd.exe -ArgumentList '/c', (Join-Path $src 'uninstall.cmd'), '/quiet' -Wait -PassThru -WindowStyle Hidden
    Check ($uninstall.ExitCode -eq 0) 'uninstall.cmd senza permessi di amministratore'
} catch {
    Say ('KO errore: ' + $_.Exception.Message)
}
Say 'fine'
