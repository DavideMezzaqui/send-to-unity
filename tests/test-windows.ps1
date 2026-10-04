# Test end-to-end di "Invia a Unity" su una vera macchina Windows (GitHub Actions).
# Installa il programma, simula Unity Hub e un editor aperto, apre il vero menu del tasto destro
# di Esplora risorse, usa le finestre e controlla i file arrivati. Esce con 1 se un controllo fallisce.

$ErrorActionPreference = 'Stop'
$conflictTitle = 'File gi' + [char]0x00E0 + ' presenti'

if ($env:GITHUB_ACTIONS -ne 'true') {
    Write-Host 'Questo test modifica il menu del tasto destro e la lista di Unity Hub: va eseguito solo su GitHub Actions.'
    exit 2
}

$src        = Split-Path -Parent $PSScriptRoot
$work       = Join-Path $env:RUNNER_TEMP 'stu'
$shots      = Join-Path $env:RUNNER_TEMP 'stu-shots'
$app        = Join-Path $env:LOCALAPPDATA 'SendToUnity'
$exe        = Join-Path $app 'SendToUnity.exe'
$log        = Join-Path $app 'log.txt'
$savedLog   = Join-Path $env:RUNNER_TEMP 'stu-log.txt'
$registry   = [Microsoft.Win32.Registry]::CurrentUser
$fileMenu   = 'Software\Classes\*\shell\SendToUnity'
$dirMenu    = 'Software\Classes\Directory\shell\SendToUnity'
$sendToLink = Join-Path ([Environment]::GetFolderPath('SendTo')) 'Progetto Unity.lnk'
$script:failures = 0
$fake = $null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Path (Join-Path $PSScriptRoot 'WinTest.cs')

# ------------------------------------------------------------------ utilita'

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "OK    $what" } else { Write-Host "FAIL  $what"; $script:failures++ }
}
function Note([string]$text) { Write-Host "INFO  $text" }
function Q([string]$text) { '"' + $text + '"' }

function New-File([string]$path, [string]$content) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $content)
    $path
}
function Read-File([string]$path) {
    if ([IO.File]::Exists($path)) { [IO.File]::ReadAllText($path) } else { '<manca>' }
}
function Exists([string]$path) { [IO.File]::Exists($path) -or [IO.Directory]::Exists($path) }

function Log-Mark {
    if ([IO.File]::Exists($log)) { (Get-Item -LiteralPath $log).Length } else { 0 }
}
function Log-Since([long]$mark) {
    if (-not [IO.File]::Exists($log)) { return '' }
    $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($mark, 'Begin')
        (New-Object IO.StreamReader($stream)).ReadToEnd()
    } finally { $stream.Dispose() }
}

# I comandi esterni possono scrivere su stderr: non deve fermare il test
function Run-Native([scriptblock]$block) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $block | Out-Host } finally { $ErrorActionPreference = $previous }
}

function Start-Tool([string[]]$arguments) {
    Start-Process -FilePath $exe -ArgumentList ($arguments -join ' ') -PassThru
}
function Wait-Exit($processes, [int]$seconds = 30) {
    $ok = $true
    foreach ($p in @($processes)) {
        if (-not $p.WaitForExit($seconds * 1000)) {
            Note "il processo $($p.Id) non si chiude: lo termino"
            try { $p.Kill() } catch { }
            $ok = $false
        }
    }
    $ok
}
# Dopo un clic nel menu non ho i processi in mano: aspetto che non ce ne siano piu'
function Wait-Idle([int]$seconds = 30) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds 500
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        if (-not (Get-Process SendToUnity -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    Note 'il programma non si chiude: lo termino'
    Get-Process SendToUnity -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    $false
}

function Find-Window([int]$procId, [string]$name, [string]$class = '*', [int]$timeoutMs = 15000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        $match = [Win]::TopWindows($procId) | Where-Object { $_.Text -eq $name -and $_.Class -like $class } | Select-Object -First 1
        if ($match) { return $match }
        Start-Sleep -Milliseconds 150
    }
    $null
}
# Finestra di qualsiasi istanza del programma (dopo un clic nel menu non so quale processo la apre)
function Find-AnyWindow([string]$name, [string]$class = '*', [int]$timeoutMs = 15000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        foreach ($p in @(Get-Process SendToUnity -ErrorAction SilentlyContinue)) {
            $match = [Win]::TopWindows($p.Id) | Where-Object { $_.Text -eq $name -and $_.Class -like $class } | Select-Object -First 1
            if ($match) { return $match }
        }
        Start-Sleep -Milliseconds 150
    }
    $null
}
function Get-Controls($window) { @([Win]::Children($window.Handle)) }
function Find-Control($window, [string]$class, [string]$text) {
    Get-Controls $window | Where-Object { $_.Class -like $class -and $_.Text -eq $text } | Select-Object -First 1
}
function Find-FolderBox($window) {
    # la seconda casella combinata dall'alto e' quella della cartella
    Get-Controls $window | Where-Object { $_.Class -like '*COMBOBOX*' } | Sort-Object Top | Select-Object -Last 1
}
function Find-MessageButton($window, [int]$id) {
    $buttons = @(Get-Controls $window | Where-Object { $_.Class -eq 'Button' })
    $match = $buttons | Where-Object { $_.Id -eq $id } | Select-Object -First 1
    if ($match) { $match } else { $buttons | Select-Object -First 1 }
}
function Show-Controls($window) {
    foreach ($c in Get-Controls $window) { Note ("  controllo: {0} | {1} | id {2}" -f $c.Class, $c.Text, $c.Id) }
}
function Press($control) {
    if (-not $control) { Note 'pulsante non trovato'; return }
    [void][Win]::PostMessage($control.Handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)   # BM_CLICK
    Start-Sleep -Milliseconds 400
}
function Set-Text($control, [string]$text) {
    if (-not $control) { Note 'casella non trovata'; return }
    # WM_SETTEXT su una casella combinata modificabile scrive nel suo campo di testo
    [void][Win]::SendMessage($control.Handle, 0x000C, [IntPtr]::Zero, $text)
}
function Save-Shot($window, [string]$name) {
    try {
        Start-Sleep -Milliseconds 500
        $bitmap = New-Object System.Drawing.Bitmap $window.Width, $window.Height
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.CopyFromScreen($window.Left, $window.Top, 0, 0, $bitmap.Size)
        $g.Dispose()
        [void][IO.Directory]::CreateDirectory($shots)
        $bitmap.Save((Join-Path $shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Note "screenshot: $name"
    } catch { Note "screenshot $name non riuscito: $($_.Exception.Message)" }
}
function Shot-Screen([string]$name) {
    try {
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.CopyFromScreen(0, 0, 0, 0, $bitmap.Size)
        $g.Dispose()
        [void][IO.Directory]::CreateDirectory($shots)
        $bitmap.Save((Join-Path $shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Note "screenshot: $name"
    } catch { Note "screenshot $name non riuscito: $($_.Exception.Message)" }
}
function Keys([string]$keys, [int]$pauseMs = 700) {
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds $pauseMs
}
# Finestra vera di Esplora risorse sulla cartella, con il file selezionato e in primo piano
function Open-Explorer([string]$folder, [string]$file) {
    Start-Process explorer.exe -ArgumentList (Q $folder)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15) {
        $window = @((New-Object -ComObject Shell.Application).Windows()) |
            Where-Object { $_.Document -and $_.Document.Folder.Self.Path -eq $folder } | Select-Object -First 1
        if ($window) {
            Start-Sleep -Seconds 1
            Select-InExplorer $window $file
            return $window
        }
        Start-Sleep -Milliseconds 300
    }
    $null
}
function Select-InExplorer($window, [string]$file) {
    $window.Document.SelectItem($window.Document.Folder.ParseName($file), 1 + 4 + 8 + 16)   # seleziona, solo lui, visibile, focus
    [void][Win]::SetForegroundWindow([IntPtr]$window.HWND)
    Start-Sleep -Milliseconds 800
}
# Tasto menu sul file; su Windows 11 "Mostra altre opzioni" (ultima voce) apre il menu classico
function Open-ClassicMenu {
    Keys '+{F10}' 1500
    if ([Environment]::OSVersion.Version.Build -ge 22000) { Keys '{UP}' 300; Keys '{ENTER}' 1500 }
}

# Testi della notifica finale (titolo | progetto e cartella | suggerimento)
function Toast-Text([int]$timeoutMs = 15000) {
    $toast = Find-AnyWindow 'Invia a Unity - notifica' '*' $timeoutMs
    if (-not $toast) { return '<nessuna notifica>' }
    (@(Get-Controls $toast | Where-Object { $_.Class -like '*STATIC*' } | ForEach-Object { $_.Text }) -join ' | ')
}
# Clic vero del mouse al centro della prima scritta della finestra
function Click-Window($window) {
    $label = Get-Controls $window | Where-Object { $_.Class -like '*STATIC*' } | Select-Object -First 1
    $target = if ($label) { $label } else { $window }
    [Win]::ClickAt($target.Left + [int]($target.Width / 2), $target.Top + [int]($target.Height / 2))
}
function Find-ExplorerWindow([string]$folder, [int]$timeoutMs = 15000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        $window = @((New-Object -ComObject Shell.Application).Windows()) |
            Where-Object { $_.Document -and $_.Document.Folder.Self.Path -eq $folder } | Select-Object -First 1
        if ($window) { return $window }
        Start-Sleep -Milliseconds 300
    }
    $null
}

# Sottomenu "Invia a Unity" per questi elementi; con $click clicca la voce che inizia cosi'
function Menu([string[]]$paths, $click = $null) {
    try {
        $items = [ExplorerMenu]::Submenu($paths, 'Invia a Unity', $click)
        if ([ExplorerMenu]::Info) { Note ('menu: ' + [ExplorerMenu]::Info) }
        if ($null -eq $items) { Note ('voci principali del menu: ' + ([ExplorerMenu]::TopItems -join ' | ')) }
        , $items
    } catch {
        Note "menu non leggibile: $($_.Exception.Message)"
        , $null
    }
}

# ------------------------------------------------------------------ test

try {
    [Win]::MoveTo(200, 200)   # il mouse lontano dall'angolo delle notifiche (sotto il mouse restano aperte)

    Write-Host '--- 1. installazione sopra la vecchia versione'
    $old = $registry.CreateSubKey("$fileMenu\command")
    $old.SetValue('', 'wscript.exe "launcher.vbs" "%1"')
    $old.Close()
    New-File (Join-Path $app 'launcher.vbs') 'vecchio' | Out-Null

    Run-Native { & (Join-Path $src 'install.cmd') /quiet }
    Check ($LASTEXITCODE -eq 0) 'install.cmd termina senza errori'
    Check (Exists $exe) 'programma compilato con il compilatore C# di Windows'
    Check (-not (Exists (Join-Path $app 'launcher.vbs'))) 'file della vecchia versione rimossi'
    $key = $registry.OpenSubKey($fileMenu)
    Check ($null -ne $key) 'voce "Invia a Unity" sui file'
    if ($key) {
        Check ($key.GetValue('MUIVerb') -eq '&Invia a Unity') 'testo della voce, con la I come scorciatoia'
        Check ($null -eq $key.OpenSubKey('command')) 'vecchia voce sostituita'
    }
    Check ($null -ne $registry.OpenSubKey($dirMenu)) 'voce "Invia a Unity" sulle cartelle'
    Check (Exists $sendToLink) 'collegamento in "Invia a"'
    if (Exists $sendToLink) {
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($sendToLink)
        Check ($link.TargetPath -eq $exe -and $link.Arguments -eq '--pick') "il collegamento avvia il programma ($($link.TargetPath) $($link.Arguments))"
    }

    Write-Host '--- 2. progetti da Unity Hub nel vero menu del tasto destro'
    $p1 = Join-Path $work 'Progetti\Gioco [Prova] & Co'
    $p2 = Join-Path $work 'Progetti\Altro progetto'
    foreach ($p in @($p1, $p2)) {
        [void][IO.Directory]::CreateDirectory((Join-Path $p 'Assets\Textures\Env'))
        [void][IO.Directory]::CreateDirectory((Join-Path $p 'ProjectSettings'))
    }
    $hubDir = Join-Path $env:APPDATA 'UnityHub'
    [void][IO.Directory]::CreateDirectory($hubDir)
    $u1 = $p1.Replace('\', '/')
    $u2 = $p2.Replace('\', '/')
    $json = '{"schema_version":"v1","data":{' +
        '"' + $u1 + '":{"title":"Gioco","path":"' + $u1 + '","lastModified":1700000000000},' +
        '"' + $u2 + '":{"title":"Altro","path":"' + $u2 + '","lastModified":1600000000000},' +
        '"C:/non/esiste":{"title":"Sparito","path":"C:/non/esiste","lastModified":1800000000000}}}'
    [IO.File]::WriteAllText((Join-Path $hubDir 'projects-v1.json'), $json)
    [void](Wait-Exit (Start-Tool @('--refresh')))

    $in = Join-Path $work 'in'
    $albedo = New-File (Join-Path $in 'roccia_albedo.png') 'A1'
    $normal = New-File (Join-Path $in 'roccia_normal.png') 'N1'
    $items = Menu @($albedo)
    Check ($null -ne $items) 'il menu del tasto destro contiene "Invia a Unity"'
    Note ('sottomenu: ' + (@($items) -join ' | '))
    Check (@($items).Count -eq 4) 'sottomenu: 2 progetti, separatore, "Scegli progetto e cartella..." (il progetto sparito escluso)'
    Check (@($items)[0] -eq 'Gioco [Prova] && Co') 'primo il progetto usato piu di recente, con la & mostrata'
    Check (@($items)[-1] -eq 'Scegli progetto e cartella...') 'ultima voce: Scegli progetto e cartella...'

    Write-Host '--- 3. primo invio verso un progetto, dal menu: si apre la finestra'
    $items = Menu @($albedo, $normal) 'Gioco'
    Check ([ExplorerMenu]::Invoked) 'clic sulla voce del progetto'
    $dialog = Find-AnyWindow 'Invia a Unity' 'WindowsForms10*'
    Check ($null -ne $dialog) 'al primo invio verso un progetto si apre la finestra'
    if ($dialog) {
        Save-Shot $dialog '1-finestra'
        Show-Controls $dialog
        $folder = Find-FolderBox $dialog
        Set-Text $folder 'Packages/sbagliata'
        Press (Find-Control $dialog '*BUTTON*' 'Invia')
        $warning = Find-AnyWindow 'Invia a Unity' '#32770'
        Check ($null -ne $warning) 'cartella fuori da Assets: compare un avviso'
        if ($warning) {
            Save-Shot $warning '2-avviso'
            Press (Find-MessageButton $warning 2)
        }
        Start-Sleep -Milliseconds 700
        Check ($null -ne (Find-AnyWindow 'Invia a Unity' 'WindowsForms10*' 2000)) "dopo l'avviso la finestra resta aperta"
        Set-Text $folder 'Assets/Textures/Rocce'
        Press (Find-Control $dialog '*BUTTON*' 'Invia')
        $toast = Find-AnyWindow 'Invia a Unity - notifica'
        Check ($null -ne $toast) 'compare la notifica'
        if ($toast) { Save-Shot $toast '3-notifica' }
    }
    Check (Wait-Idle) 'il programma si chiude da solo'
    $dest = Join-Path $p1 'Assets\Textures\Rocce'
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A1' -and (Read-File (Join-Path $dest 'roccia_normal.png')) -eq 'N1') 'i 2 file sono nella cartella scelta'
    Check (Exists $albedo) 'copiando, gli originali restano'
    $items = Menu @($albedo)
    Check ((@($items)[0]) -like '*Assets/Textures/Rocce') "il menu mostra la cartella ricordata ($(@($items)[0]))"

    Write-Host '--- 4. invio rapido dal menu: 3 file, poi 40 file (oltre il limite di 15 di Windows)'
    $three = @(1..3 | ForEach-Object { New-File (Join-Path $in "tre\tex_$_.png") "T$_" })
    $mark = Log-Mark
    $items = Menu $three 'Gioco'
    Check ([ExplorerMenu]::Invoked) 'clic sulla voce del progetto con 3 file'
    $toast = Find-AnyWindow 'Invia a Unity - notifica'
    Check ($null -ne $toast) 'nessuna finestra, solo la notifica'
    Check (Wait-Idle) 'il programma si chiude da solo'
    $missing = @($three | Where-Object { -not (Exists (Join-Path $dest ([IO.Path]::GetFileName($_)))) })
    Check ($missing.Count -eq 0) "arrivati i 3 file (mancano: $($missing.Count))"
    Note ("istanze che hanno copiato: " + ([regex]::Matches((Log-Since $mark), 'leader to:')).Count)

    $special = 't' + [char]0x00E8 + 'x ' + [char]0x00FC + ' ' + [char]0x4E2D + [char]0x6587 + '.png'
    $many = @(1..39 | ForEach-Object { New-File (Join-Path $in "molti\frame_$('{0:d2}' -f $_).png") "F$_" })
    $many += New-File (Join-Path $in "molti\$special") 'U'
    $mark = Log-Mark
    $items = Menu $many 'Gioco'
    Check ($null -ne $items) 'con 40 file la voce "Invia a Unity" resta nel menu'
    Check ([ExplorerMenu]::Invoked) 'clic sulla voce del progetto con 40 file'
    Check (Wait-Idle 90) 'tutte le istanze si chiudono'
    $missing = @($many | Where-Object { -not (Exists (Join-Path $dest ([IO.Path]::GetFileName($_)))) })
    Check ($missing.Count -eq 0) "arrivati tutti i 40 file, anche quello con caratteri speciali (mancano: $($missing.Count))"
    $leaders = ([regex]::Matches((Log-Since $mark), 'leader to:')).Count
    Note "istanze che hanno copiato: $leaders su 40 avviate da Explorer"
    Check ($leaders -ge 1 -and $leaders -le 2) 'i file sono stati raccolti da una sola istanza (al massimo due)'
    $toasts = ([regex]::Matches((Log-Since $mark), 'notifica: ')).Count
    Check ($toasts -eq $leaders) "una sola notifica per invio, a copie finite (notifiche: $toasts)"

    # un secondo invio mentre la notifica del primo e' ancora visibile
    $first = New-File (Join-Path $in 'subito\primo.png') 'P1'
    $second = New-File (Join-Path $in 'subito\secondo.png') 'P2'
    $mark = Log-Mark
    $proc = Start-Tool @('--to', (Q $p1), (Q $first))
    Check ($null -ne (Find-Window $proc.Id 'Invia a Unity - notifica')) 'notifica del primo invio'
    Start-Sleep -Seconds 2   # una persona impiega almeno questo per il secondo tasto destro
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $proc2 = Start-Tool @('--to', (Q $p1), (Q $second))
    while ($watch.ElapsedMilliseconds -lt 10000 -and -not (Exists (Join-Path $dest 'secondo.png'))) { Start-Sleep -Milliseconds 50 }
    $elapsed = $watch.ElapsedMilliseconds
    Check ($elapsed -lt 2000) "un secondo invio mentre la notifica e' visibile arriva subito ($elapsed ms)"
    [void](Wait-Exit @($proc, $proc2))
    $counts = @([regex]::Matches((Log-Since $mark), 'notifica: (\d+) copiati') | ForEach-Object { $_.Groups[1].Value })
    Check (($counts -join ',') -eq '1,1') "ogni invio ha la sua notifica con il suo conteggio ($($counts -join ','))"

    Write-Host '--- 5. file gia presenti'
    [IO.File]::WriteAllText($albedo, 'A2')
    $proc = Start-Tool @('--to', (Q $p1), (Q $albedo))
    $ask = Find-Window $proc.Id $conflictTitle 'WindowsForms10*'
    Check ($null -ne $ask) 'chiede se sostituire un file gia presente'
    if ($ask) {
        Save-Shot $ask '4-sostituire'
        Press (Find-Control $ask '*BUTTON*' 'Sostituisci')
    }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A2') 'con Si il file viene sostituito'

    [IO.File]::WriteAllText($albedo, 'A3')
    $proc = Start-Tool @('--to', (Q $p1), (Q $albedo))
    $ask = Find-Window $proc.Id $conflictTitle 'WindowsForms10*'
    if ($ask) { Press (Find-Control $ask '*BUTTON*' 'Salta questi') }
    $text = Toast-Text
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A2') "con No il file resta com'era"
    Check ($text -like 'Nessun file inviato: era gi* presente*') "la notifica dice che non e' arrivato niente ($text)"

    # un file che sta gia' proprio nella cartella di destinazione
    $proc = Start-Tool @('--to', (Q $p1), (Q (Join-Path $dest 'roccia_albedo.png')))
    $text = Toast-Text
    [void](Wait-Exit $proc)
    Check ($text -like 'I file sono gi* in questa cartella*') "file gia' nella cartella: lo dice la notifica ($text)"

    # Unity sta importando il file e lo tiene bloccato: il programma aspetta e riprova
    [IO.File]::WriteAllText($albedo, 'A4')
    $mark = Log-Mark
    $lock = [IO.File]::Open((Join-Path $dest 'roccia_albedo.png'), 'Open', 'Read', 'None')
    try {
        $proc = Start-Tool @('--to', (Q $p1), (Q $albedo))
        $ask = Find-Window $proc.Id $conflictTitle 'WindowsForms10*'
        if ($ask) { Press (Find-Control $ask '*BUTTON*' 'Sostituisci') }
        $progressWindow = Find-Window $proc.Id 'Invia a Unity - avanzamento' '*' 2500
        Check ($null -ne $progressWindow) 'se la copia va per le lunghe compare la notifica di avanzamento'
        if ($progressWindow) { Save-Shot $progressWindow '9-avanzamento' }
    } finally { $lock.Dispose() }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A4') 'file bloccato per un po (Unity che importa): il programma aspetta e lo sostituisce'
    Check (-not (Log-Since $mark).Contains('errore su')) 'nessun errore per il file bloccato'

    Write-Host '--- 6. cartelle, dal menu'
    $flip = Join-Path $in 'Flipbook'
    New-File (Join-Path $flip 'frame_01.png') 'F1' | Out-Null
    New-File (Join-Path $flip 'sub\frame_02.png') 'F2' | Out-Null
    New-File (Join-Path $flip 'Thumbs.db') 'x' | Out-Null
    New-File (Join-Path $flip '.git\config') 'git' | Out-Null
    New-File (Join-Path $flip '__MACOSX\frame_01.png') 'mac' | Out-Null
    New-File (Join-Path $flip '._frame_01.png') 'mac' | Out-Null
    [void][IO.Directory]::CreateDirectory((Join-Path $flip 'vuota'))
    $items = Menu @($flip) 'Gioco'
    Check ([ExplorerMenu]::Invoked) 'la voce compare anche sulle cartelle'
    [void](Wait-Idle)
    $flipDest = Join-Path $dest 'Flipbook'
    Check ((Exists (Join-Path $flipDest 'frame_01.png')) -and (Exists (Join-Path $flipDest 'sub\frame_02.png'))) 'cartella copiata con le sottocartelle'
    Check (-not (Exists (Join-Path $flipDest 'Thumbs.db'))) 'Thumbs.db non viene copiato'
    Check (-not (Exists (Join-Path $flipDest '.git')) -and -not (Exists (Join-Path $flipDest '__MACOSX')) -and -not (Exists (Join-Path $flipDest '._frame_01.png'))) 'cartelle e file spazzatura (.git, __MACOSX, ._) non copiati'
    Check (Exists (Join-Path $flipDest 'vuota')) 'copiate anche le cartelle vuote'
    New-File (Join-Path $flip 'frame_03.png') 'F3' | Out-Null
    $proc = Start-Tool @('--to', (Q $p1), (Q $flip))
    $ask = Find-Window $proc.Id $conflictTitle 'WindowsForms10*'
    if ($ask) { Press (Find-Control $ask '*BUTTON*' 'Salta questi') }
    [void](Wait-Exit $proc)
    Check (Exists (Join-Path $flipDest 'frame_03.png')) 'al secondo invio il file nuovo si aggiunge'
    Check (-not (Exists (Join-Path $flipDest 'Flipbook'))) 'nessuna cartella annidata (Flipbook dentro Flipbook)'

    # file e cartelle selezionati insieme, in tutti e due gli ordini
    $looseA = New-File (Join-Path $in 'misto\sciolto_a.png') 'SA'
    $looseB = New-File (Join-Path $in 'misto\sciolto_b.png') 'SB'
    $packA = Join-Path $in 'misto\Pacchetto_A'
    $packB = Join-Path $in 'misto\Pacchetto_B'
    New-File (Join-Path $packA 'dentro_a.png') 'DA' | Out-Null
    New-File (Join-Path $packB 'dentro_b.png') 'DB' | Out-Null
    $items = Menu @($looseA, $packA) 'Gioco'
    Check ([ExplorerMenu]::Invoked) 'prima un file e poi una cartella: la voce compare'
    [void](Wait-Idle)
    $items = Menu @($packB, $looseB) 'Gioco'
    Check ([ExplorerMenu]::Invoked) 'prima una cartella e poi un file: la voce compare'
    [void](Wait-Idle)
    $arrived = @('sciolto_a.png', 'Pacchetto_A\dentro_a.png', 'sciolto_b.png', 'Pacchetto_B\dentro_b.png' | Where-Object { Exists (Join-Path $dest $_) })
    Check ($arrived.Count -eq 4) "file e cartelle insieme: arriva tutto ($($arrived -join ', '))"

    Write-Host '--- 7. progetto aperto in Unity'
    $fakeDir = Join-Path $work 'fake'
    [void][IO.Directory]::CreateDirectory($fakeDir)
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    Run-Native { & $csc /nologo /target:winexe "/out:$fakeDir\Unity.exe" /r:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'FakeUnity.cs') }
    Copy-Item (Join-Path $fakeDir 'Unity.exe') (Join-Path $fakeDir 'Altro.exe')
    $fake = Start-Process (Join-Path $fakeDir 'Unity.exe') -ArgumentList ('-projectPath ' + (Q $p2)) -PassThru
    $fakeTitle = (Split-Path $p2 -Leaf) + ' - SampleScene - Windows, Mac, Linux - Unity 6000.0.23f1 <DX11>'
    $fakeWindow = Find-Window $fake.Id $fakeTitle
    Check ($null -ne $fakeWindow) 'Unity finto avviato'
    $fakeHandle = [IntPtr]::Zero
    if ($fakeWindow) { $fakeHandle = $fakeWindow.Handle }

    $smoke = New-File (Join-Path $in 'fumo.png') 'S1'
    $mark = Log-Mark
    $proc = Start-Tool @('--to', (Q $p2), (Q $smoke))
    $dialog = Find-Window $proc.Id 'Invia a Unity' 'WindowsForms10*'
    if ($dialog) {
        Save-Shot $dialog '5-finestra-unity-aperto'
        Set-Text (Find-FolderBox $dialog) 'Assets/VFX'
        Press (Find-Control $dialog '*BUTTON*' 'Invia')
    }
    [void](Wait-Exit $proc)
    $text = Log-Since $mark
    Check ($text.Contains("progetto $p2 (aperto)")) 'progetto con Unity aperto riconosciuto'
    Check ((Read-File (Join-Path $p2 'Assets\VFX\fumo.png')) -eq 'S1') 'file arrivato nel progetto aperto'
    Check ([Win]::GetForegroundWindow() -eq $fakeHandle) "Unity portato in primo piano dopo l'invio"

    # invio rapido mentre in primo piano c'e' un'altra finestra
    $other = Start-Process (Join-Path $fakeDir 'Altro.exe') -PassThru
    $otherWindow = Find-Window $other.Id 'Altra finestra'
    if ($otherWindow) { [void][Win]::SetForegroundWindow($otherWindow.Handle) }
    Start-Sleep -Milliseconds 500
    Note "in primo piano prima dell'invio rapido: $([Win]::GetForegroundWindow()) (Unity finto: $fakeHandle)"
    [IO.File]::WriteAllText($smoke, 'S2')
    $proc = Start-Tool @('--to', (Q $p2), (Q $smoke))
    $ask = Find-Window $proc.Id $conflictTitle 'WindowsForms10*'
    if ($ask) { Press (Find-Control $ask '*BUTTON*' 'Sostituisci') }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $p2 'Assets\VFX\fumo.png')) -eq 'S2') 'invio rapido verso il progetto aperto'
    Check ([Win]::GetForegroundWindow() -eq $fakeHandle) "Unity portato in primo piano anche con l'invio rapido"
    try { $other.Kill() } catch { }

    Write-Host '--- 8. sposta, dalla voce "Scegli progetto e cartella..."'
    $moving = New-File (Join-Path $in 'da_spostare.png') 'M'
    $items = Menu @($moving) 'Scegli progetto'
    $dialog = Find-AnyWindow 'Invia a Unity' 'WindowsForms10*'
    if ($dialog) {
        Save-Shot $dialog '6-finestra-scegli'
        Press (Find-Control $dialog '*BUTTON*' 'Sposta invece di copiare (gli originali vengono tolti)')
        Set-Text (Find-FolderBox $dialog) 'Assets/Spostati'
        Press (Find-Control $dialog '*BUTTON*' 'Invia')
    }
    [void](Wait-Idle)
    Check ((Read-File (Join-Path $p2 'Assets\Spostati\da_spostare.png')) -eq 'M') 'file spostato nel progetto aperto (proposto per primo)'
    Check (-not (Exists $moving)) "spostando, l'originale sparisce"
    try { $fake.Kill() } catch { }

    Write-Host '--- 9. il vero Esplora risorse, da tastiera'
    $realDir = Join-Path $work 'esplora'
    $realFile = New-File (Join-Path $realDir 'texture_vera.png') 'R'
    $mark = Log-Mark
    $explorer = Open-Explorer $realDir 'texture_vera.png'
    Check ($null -ne $explorer) 'finestra di Esplora risorse aperta sul file'
    if ($explorer) {
        Open-ClassicMenu
        Shot-Screen '7-menu-classico'
        Keys 'i' 1200
        Shot-Screen '8-sottomenu'
        Keys '{ESC}' 300; Keys '{ESC}' 300; Keys '{ESC}' 300
        Start-Sleep -Seconds 4
        Check (-not (Log-Since $mark).Contains('leader')) 'aprire e chiudere il menu senza scegliere non avvia il programma'

        # adesso la scelta vera: tasto menu, I, primo progetto (il piu' recente: Altro progetto)
        Select-InExplorer $explorer 'texture_vera.png'
        Open-ClassicMenu
        Keys 'i' 1000
        Keys '{RIGHT}' 500
        Keys '{ENTER}' 500
        [void](Wait-Idle)
        Check ((Read-File (Join-Path $p2 'Assets\Spostati\texture_vera.png')) -eq 'R') 'dal vero menu di Esplora risorse il file arriva nel progetto'
        try { $explorer.Quit() } catch { }
    }

    Write-Host '--- 10. file .meta di Unity'
    $p3 = Join-Path $work 'Altri\Gioco [Prova] & Co'
    [void][IO.Directory]::CreateDirectory((Join-Path $p3 'ProjectSettings'))
    $metaA = New-File (Join-Path $p3 'Assets\Tex\meta_a.png') 'MA'
    New-File "$metaA.meta" 'GUID-A' | Out-Null
    [void](Wait-Exit (Start-Tool @('--to', (Q $p1), (Q $metaA))))
    Check ((Read-File (Join-Path $dest 'meta_a.png.meta')) -eq 'GUID-A') 'copiando da un altro progetto il .meta arriva con il file (impostazioni di import salve)'

    [IO.File]::WriteAllText($metaA, 'MA2')
    [IO.File]::WriteAllText("$metaA.meta", 'GUID-B')
    $proc = Start-Tool @('--to', (Q $p1), (Q $metaA))
    $ask = Find-Window $proc.Id $conflictTitle 'WindowsForms10*'
    if ($ask) { Press (Find-Control $ask '*BUTTON*' 'Sostituisci') }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'meta_a.png')) -eq 'MA2' -and (Read-File (Join-Path $dest 'meta_a.png.meta')) -eq 'GUID-A') 'sostituendo il file, il .meta del progetto resta (riferimenti salvi)'

    $inner = New-File (Join-Path $p1 'Assets\Textures\Env\interno.png') 'I'
    New-File "$inner.meta" 'GUID-I' | Out-Null
    [void](Wait-Exit (Start-Tool @('--to', (Q $p1), (Q $inner))))
    Check ((Exists (Join-Path $dest 'interno.png')) -and -not (Exists (Join-Path $dest 'interno.png.meta'))) 'copiando dentro lo stesso progetto il .meta non si duplica (niente GUID doppi)'

    $metaB = New-File (Join-Path $p3 'Assets\Tex\meta_b.png') 'MB'
    New-File "$metaB.meta" 'GUID-MB' | Out-Null
    $mark = Log-Mark
    $items = Menu @($metaB, "$metaB.meta") 'Gioco'
    [void](Wait-Idle)
    Check ((Read-File (Join-Path $dest 'meta_b.png.meta')) -eq 'GUID-MB') 'file e .meta selezionati insieme: arrivano entrambi'
    Check ((Log-Since $mark) -match 'notifica: 1 copiati') 'il .meta non conta come file inviato'

    $kit = Join-Path $p3 'Assets\Kit'
    New-File (Join-Path $kit 'k1.png') 'K1' | Out-Null
    New-File (Join-Path $kit 'k1.png.meta') 'GUID-K1' | Out-Null
    New-File (Join-Path $kit 'sub\k2.png') 'K2' | Out-Null
    New-File (Join-Path $kit 'sub\k2.png.meta') 'GUID-K2' | Out-Null
    New-File (Join-Path $kit 'sub.meta') 'GUID-SUB' | Out-Null
    New-File "$kit.meta" 'GUID-KIT' | Out-Null
    New-File (Join-Path $kit 'sub\cancellato.png.meta') 'GUID-ORFANO' | Out-Null
    New-File (Join-Path $kit 'Thumbs.db') 'cache' | Out-Null
    New-File (Join-Path $kit 'Thumbs.db.meta') 'GUID-THUMBS' | Out-Null
    [IO.File]::SetAttributes((Join-Path $kit 'Thumbs.db'), 'ReadOnly, Hidden, System')
    [IO.File]::SetAttributes((Join-Path $kit 'k1.png'), 'ReadOnly')
    $proc = Start-Tool @('--pick', (Q $kit))
    $dialog = Find-Window $proc.Id 'Invia a Unity' 'WindowsForms10*'
    if ($dialog) {
        Press (Find-Control $dialog '*BUTTON*' 'Sposta invece di copiare (gli originali vengono tolti)')
        Set-Text (Find-FolderBox $dialog) 'Assets/Kits'
        Press (Find-Control $dialog '*BUTTON*' 'Invia')
    }
    [void](Wait-Exit $proc)
    $kits = Join-Path $p1 'Assets\Kits'
    $kitDest = Join-Path $kits 'Kit'
    Check ((Read-File (Join-Path $kitDest 'k1.png.meta')) -eq 'GUID-K1' -and (Read-File (Join-Path $kitDest 'sub\k2.png.meta')) -eq 'GUID-K2') 'spostando una cartella i .meta dei file la seguono'
    Check ((Read-File (Join-Path $kitDest 'sub.meta')) -eq 'GUID-SUB' -and (Read-File "$kitDest.meta") -eq 'GUID-KIT') 'anche i .meta delle cartelle la seguono'
    Check ((Read-File (Join-Path $kitDest 'k1.png')) -eq 'K1') 'anche un file in sola lettura viene spostato'
    Check ((Read-File (Join-Path $kitDest 'sub\cancellato.png.meta')) -eq 'GUID-ORFANO') 'un .meta rimasto senza file segue la sua cartella'
    Check (-not (Exists (Join-Path $kitDest 'Thumbs.db')) -and -not (Exists (Join-Path $kitDest 'Thumbs.db.meta'))) 'Thumbs.db e il suo .meta non finiscono nel progetto'
    Check (-not (Exists $kit) -and -not (Exists "$kit.meta")) "nell'origine non restano cartelle vuote ne' .meta orfani"

    Write-Host '--- 11. progetti con lo stesso nome'
    $u3 = $p3.Replace('\', '/')
    $json = '{"schema_version":"v1","data":{' +
        '"' + $u1 + '":{"title":"Gioco","path":"' + $u1 + '","lastModified":1700000000000},' +
        '"' + $u2 + '":{"title":"Altro","path":"' + $u2 + '","lastModified":1600000000000},' +
        '"' + $u3 + '":{"title":"Gioco","path":"' + $u3 + '","lastModified":1500000000000}}}'
    [IO.File]::WriteAllText((Join-Path $hubDir 'projects-v1.json'), $json)
    [void](Wait-Exit (Start-Tool @('--refresh')))
    $items = @(Menu @($albedo) | ForEach-Object { $_ })
    Note ('sottomenu: ' + ($items -join ' | '))
    $fromProgetti = @($items | Where-Object { $_ -like 'Gioco `[Prova`] && Co  (Progetti)*' }).Count
    $fromAltri = @($items | Where-Object { $_ -like 'Gioco `[Prova`] && Co  (Altri)*' }).Count
    Check ($fromProgetti -eq 1 -and $fromAltri -eq 1) 'due progetti con lo stesso nome si distinguono dalla cartella che li contiene'

    Write-Host '--- 12. clic sulla notifica'
    $clickFile = New-File (Join-Path $in 'clic.png') 'C'
    $mark = Log-Mark
    $proc = Start-Tool @('--to', (Q $p1), (Q $clickFile))
    $toast = Find-Window $proc.Id 'Invia a Unity - notifica'
    Check ($null -ne $toast) 'compare la notifica'
    if ($toast) {
        [Win]::MoveTo($toast.Left + 20, $toast.Top + 10)
        Start-Sleep -Seconds 5
        Check ($null -ne (Find-Window $proc.Id 'Invia a Unity - notifica' '*' 500)) 'con il mouse sopra, la notifica non sparisce'
        Click-Window $toast
        [Win]::MoveTo(200, 200)
    }
    [void](Wait-Exit $proc)
    Note ('dal log: ' + (((Log-Since $mark) -split "`n" | Where-Object { $_ -match 'apro |non aperta' }) -join ' | '))
    $window = Find-ExplorerWindow $kits
    Check ($null -ne $window) 'cliccando la notifica si apre la cartella del progetto'
    if ($window) {
        # la vista si completa dopo l'apertura: rileggo la finestra finche' la selezione non compare
        $wanted = Join-Path $kits 'clic.png'
        $selected = @()
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.ElapsedMilliseconds -lt 10000) {
            $current = Find-ExplorerWindow $kits 1000
            if ($current) {
                $window = $current
                try { $selected = @($current.Document.SelectedItems() | ForEach-Object { $_.Path }) } catch { }
            }
            if ($selected -contains $wanted) { break }
            Start-Sleep -Milliseconds 500
        }
        if (-not ($selected -contains $wanted)) {
            try { Note ('elemento con il focus: ' + $window.Document.FocusedItem.Path) } catch { }
            Shot-Screen '10-cartella-aperta'
        }
        Check ($selected -contains $wanted) "con il file appena arrivato gia' selezionato ($($selected -join ', '))"
        try { $window.Quit() } catch { }
    }

    Write-Host '--- 13. aggiornamento: reinstallare non perde le impostazioni'
    Run-Native { & (Join-Path $src 'install.cmd') /quiet }
    Check ($LASTEXITCODE -eq 0) 'reinstallazione senza errori'
    $label = [string]$registry.OpenSubKey("$fileMenu\shell\00").GetValue('MUIVerb')
    Check ($label -like '*Assets/Kits') "dopo la reinstallazione il menu ricorda ancora la cartella ($label)"

    Write-Host '--- 14. disinstallazione'
    Copy-Item -LiteralPath $log -Destination $savedLog -ErrorAction SilentlyContinue
    Run-Native { & (Join-Path $src 'uninstall.cmd') /quiet }
    Check ($null -eq $registry.OpenSubKey($fileMenu)) 'voce sui file rimossa'
    Check ($null -eq $registry.OpenSubKey($dirMenu)) 'voce sulle cartelle rimossa'
    Check (-not (Exists $sendToLink)) 'collegamento in "Invia a" rimosso'
    Check (-not (Exists $app)) 'cartella del programma rimossa'
    Check ($null -eq (Menu @($albedo))) 'la voce non compare piu nel menu'
}
catch {
    Write-Host "FAIL  errore nel test: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    $script:failures++
}
finally {
    if ($fake) { try { $fake.Kill() } catch { } }
    Get-Process SendToUnity -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

    Write-Host '--- log del programma'
    $shown = if (Exists $savedLog) { $savedLog } else { $log }
    if (Exists $shown) { Get-Content -LiteralPath $shown -Encoding UTF8 | Select-Object -Last 150 | ForEach-Object { Write-Host "  $_" } }

    # screenshot in base64 nel log, per controllare l'aspetto delle finestre
    if (Test-Path $shots) {
        foreach ($png in Get-ChildItem $shots -Filter *.png) {
            $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($png.FullName))
            for ($i = 0; $i -lt $b64.Length; $i += 4000) {
                Write-Host ('SHOT ' + $png.BaseName + ' ' + $b64.Substring($i, [Math]::Min(4000, $b64.Length - $i)))
            }
        }
    }
}

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) controlli FALLITI"
    exit 1
}
Write-Host 'Tutti i controlli superati'
exit 0
