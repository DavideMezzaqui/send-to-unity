# Test end-to-end di "Invia a Unity" su una vera macchina Windows (GitHub Actions).
# Installa il programma, simula Unity Hub e un editor aperto, usa le finestre con UI Automation
# e controlla i file arrivati nei progetti. Esce con 1 se un controllo fallisce.

$ErrorActionPreference = 'Stop'

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

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
'@
$AE    = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$Types = [System.Windows.Automation.ControlType]

function Find-Window([int]$procId, [string]$name, [string]$class = '*', [int]$timeoutMs = 15000) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        foreach ($w in $AE::RootElement.FindAll($Scope::Children, $condition)) {
            try {
                if ($w.Current.Name -eq $name -and $w.Current.ClassName -like $class) { return $w }
            } catch { }
        }
        Start-Sleep -Milliseconds 150
    }
    $null
}
function Find-Id($parent, [string]$id) {
    $parent.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)))
}
function Find-Type($parent, $type) {
    $parent.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))
}
function Press($element) {
    if (-not $element) { Note 'pulsante non trovato'; return }
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        # Invoke puo' restare bloccato se il clic apre una finestra modale: lo eseguo in un thread a parte
        $ps = [PowerShell]::Create()
        [void]$ps.AddScript({ param($p) $p.Invoke() }).AddArgument($pattern)
        [void]$ps.BeginInvoke()
    } else {
        [void][Win]::PostMessage([IntPtr]$element.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)   # BM_CLICK
    }
    Start-Sleep -Milliseconds 300
}
function Toggle($element) {
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
    else { [void][Win]::PostMessage([IntPtr]$element.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
    Start-Sleep -Milliseconds 200
}
function Set-Text($combo, [string]$text) {
    # WM_SETTEXT su una casella combinata modificabile scrive nel suo campo di testo
    [void][Win]::SendMessage([IntPtr]$combo.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $text)
}
function Save-Shot($element, [string]$name) {
    try {
        Start-Sleep -Milliseconds 500
        $r = $element.Current.BoundingRectangle
        $bitmap = New-Object System.Drawing.Bitmap ([int][Math]::Ceiling($r.Width)), ([int][Math]::Ceiling($r.Height))
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bitmap.Size)
        $g.Dispose()
        [void][IO.Directory]::CreateDirectory($shots)
        $bitmap.Save((Join-Path $shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Note "screenshot: $name"
    } catch { Note "screenshot $name non riuscito: $($_.Exception.Message)" }
}

# ------------------------------------------------------------------ test

try {
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
        Check ($key.GetValue('MUIVerb') -eq 'Invia a Unity') 'testo della voce'
        Check ($null -eq $key.OpenSubKey('command')) 'vecchia voce sostituita'
        Check ($null -ne $key.OpenSubKey('shell\99\command')) 'sottovoce "Scegli progetto e cartella..."'
    }
    Check ($null -ne $registry.OpenSubKey($dirMenu)) 'voce "Invia a Unity" sulle cartelle'
    Check (Exists $sendToLink) 'collegamento in "Invia a"'
    if (Exists $sendToLink) {
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($sendToLink)
        Check ($link.TargetPath -eq $exe -and $link.Arguments -eq '--pick') "il collegamento avvia il programma ($($link.TargetPath) $($link.Arguments))"
    }

    Write-Host '--- 2. progetti e Unity Hub'
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
    $menu = $registry.OpenSubKey("$fileMenu\shell")
    $names = @($menu.GetSubKeyNames())
    Check ($names.Count -eq 3) "sottomenu con 2 progetti + Scegli, il progetto sparito escluso (voci: $($names -join ', '))"
    $firstLabel = [string]$menu.OpenSubKey('00').GetValue('MUIVerb')
    Check ($firstLabel -eq 'Gioco [Prova] && Co') "primo il progetto usato piu' di recente, con la & raddoppiata ($firstLabel)"
    $command = [string]$menu.OpenSubKey('00\command').GetValue('')
    Check ($command -eq ((Q $exe) + ' --to ' + (Q $p1) + ' "%1"')) "comando del progetto: $command"
    Check ($menu.OpenSubKey('99').GetValue('CommandFlags') -eq 0x20) 'separatore prima di "Scegli progetto e cartella..."'

    Write-Host '--- 3. primo invio verso un progetto: si apre la finestra'
    $in = Join-Path $work 'in'
    $albedo = New-File (Join-Path $in 'roccia_albedo.png') 'A1'
    $normal = New-File (Join-Path $in 'roccia_normal.png') 'N1'
    $proc = Start-Tool @('--to', (Q $p1), (Q $albedo), (Q $normal))
    $dialog = Find-Window $proc.Id 'Invia a Unity' 'WindowsForms10*'
    Check ($null -ne $dialog) 'si apre la finestra'
    if ($dialog) {
        Save-Shot $dialog '1-finestra'
        $pathText = (Find-Id $dialog 'pathLabel').Current.Name
        Check ($pathText -eq $p1) "il percorso del progetto si legge intero ($pathText)"
        $folder = Find-Id $dialog 'folderCombo'
        Set-Text $folder 'Packages/sbagliata'
        Press (Find-Id $dialog 'sendButton')
        $warning = Find-Window $proc.Id 'Invia a Unity' '#32770'
        Check ($null -ne $warning) 'cartella fuori da Assets: compare un avviso'
        if ($warning) {
            Save-Shot $warning '2-avviso'
            Press (Find-Type $warning $Types::Button)
        }
        Start-Sleep -Milliseconds 700
        Check (-not $proc.HasExited) "dopo l'avviso la finestra resta aperta"
        Set-Text $folder 'Assets/Textures/Rocce'
        Press (Find-Id $dialog 'sendButton')
        $toast = Find-Window $proc.Id 'Invia a Unity - notifica'
        Check ($null -ne $toast) 'compare la notifica'
        if ($toast) { Save-Shot $toast '3-notifica' }
    }
    Check (Wait-Exit $proc) 'il programma si chiude da solo'
    $dest = Join-Path $p1 'Assets\Textures\Rocce'
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A1' -and (Read-File (Join-Path $dest 'roccia_normal.png')) -eq 'N1') 'i 2 file sono nella cartella scelta'
    Check (Exists $albedo) 'copiando, gli originali restano'
    $label = [string]$registry.OpenSubKey("$fileMenu\shell\00").GetValue('MUIVerb')
    Check ($label -like '*Assets/Textures/Rocce') "il menu mostra la cartella ricordata ($label)"

    Write-Host '--- 4. 16 file selezionati insieme (una istanza per file, come fa Explorer)'
    $batch = @(1..15 | ForEach-Object { New-File (Join-Path $in "lotto\tex_$_.png") "T$_" })
    $special = 't' + [char]0x00E8 + 'x ' + [char]0x00FC + ' ' + [char]0x4E2D + [char]0x6587 + '.png'
    $batch += New-File (Join-Path $in "lotto\$special") 'U'
    $mark = Log-Mark
    $procs = @(foreach ($f in $batch) { Start-Tool @('--to', (Q $p1), (Q $f)) })
    Check (Wait-Exit $procs 60) 'tutte le istanze si chiudono'
    $missing = @($batch | Where-Object { -not (Exists (Join-Path $dest ([IO.Path]::GetFileName($_)))) })
    Check ($missing.Count -eq 0) "arrivati tutti i 16 file, anche quello con caratteri speciali (mancano: $($missing.Count))"
    $leaders = ([regex]::Matches((Log-Since $mark), 'leader to:')).Count
    Note "istanze che hanno copiato: $leaders su 16 avviate"
    Check ($leaders -ge 1 -and $leaders -le 2) 'i file sono stati raccolti da una sola istanza (al massimo due)'

    Write-Host '--- 5. file gia presenti'
    [IO.File]::WriteAllText($albedo, 'A2')
    $proc = Start-Tool @('--to', (Q $p1), (Q $albedo))
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    Check ($null -ne $ask) 'chiede se sostituire un file gia presente'
    if ($ask) {
        Save-Shot $ask '4-sostituire'
        Press (Find-Id $ask '6')   # 6 = Si'
    }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A2') 'con Si il file viene sostituito'

    [IO.File]::WriteAllText($albedo, 'A3')
    $proc = Start-Tool @('--to', (Q $p1), (Q $albedo))
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    if ($ask) { Press (Find-Id $ask '7') }   # 7 = No
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A2') "con No il file resta com'era"

    Write-Host '--- 6. cartelle'
    $flip = Join-Path $in 'Flipbook'
    New-File (Join-Path $flip 'frame_01.png') 'F1' | Out-Null
    New-File (Join-Path $flip 'sub\frame_02.png') 'F2' | Out-Null
    New-File (Join-Path $flip 'Thumbs.db') 'x' | Out-Null
    [void][IO.Directory]::CreateDirectory((Join-Path $flip 'vuota'))
    [void](Wait-Exit (Start-Tool @('--to', (Q $p1), (Q $flip))))
    $flipDest = Join-Path $dest 'Flipbook'
    Check ((Exists (Join-Path $flipDest 'frame_01.png')) -and (Exists (Join-Path $flipDest 'sub\frame_02.png'))) 'cartella copiata con le sottocartelle'
    Check (-not (Exists (Join-Path $flipDest 'Thumbs.db'))) 'Thumbs.db non viene copiato'
    Check (Exists (Join-Path $flipDest 'vuota')) 'copiate anche le cartelle vuote'
    New-File (Join-Path $flip 'frame_03.png') 'F3' | Out-Null
    $proc = Start-Tool @('--to', (Q $p1), (Q $flip))
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    if ($ask) { Press (Find-Id $ask '7') }
    [void](Wait-Exit $proc)
    Check (Exists (Join-Path $flipDest 'frame_03.png')) 'al secondo invio il file nuovo si aggiunge'
    Check (-not (Exists (Join-Path $flipDest 'Flipbook'))) 'nessuna cartella annidata (Flipbook dentro Flipbook)'

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
    if ($fakeWindow) { $fakeHandle = [IntPtr]$fakeWindow.Current.NativeWindowHandle }

    $smoke = New-File (Join-Path $in 'fumo.png') 'S1'
    $mark = Log-Mark
    $proc = Start-Tool @('--to', (Q $p2), (Q $smoke))
    $dialog = Find-Window $proc.Id 'Invia a Unity' 'WindowsForms10*'
    if ($dialog) {
        Save-Shot $dialog '5-finestra-unity-aperto'
        Set-Text (Find-Id $dialog 'folderCombo') 'Assets/VFX'
        Press (Find-Id $dialog 'sendButton')
    }
    [void](Wait-Exit $proc)
    $text = Log-Since $mark
    Check ($text.Contains("progetto $p2 (aperto)")) 'progetto con Unity aperto riconosciuto'
    Check ((Read-File (Join-Path $p2 'Assets\VFX\fumo.png')) -eq 'S1') 'file arrivato nel progetto aperto'
    Note ('dal log: ' + (($text -split "`n" | Where-Object { $_ -match 'primo piano|finestra di Unity' }) -join ' | '))
    Check ([Win]::GetForegroundWindow() -eq $fakeHandle) "Unity portato in primo piano dopo l'invio"

    # invio rapido mentre in primo piano c'e' un'altra finestra
    $other = Start-Process (Join-Path $fakeDir 'Altro.exe') -PassThru
    $otherWindow = Find-Window $other.Id 'Altra finestra'
    if ($otherWindow) { [void][Win]::SetForegroundWindow([IntPtr]$otherWindow.Current.NativeWindowHandle) }
    Start-Sleep -Milliseconds 500
    Note "in primo piano prima dell'invio rapido: $([Win]::GetForegroundWindow()) (Unity finto: $fakeHandle)"
    [IO.File]::WriteAllText($smoke, 'S2')
    $mark = Log-Mark
    $proc = Start-Tool @('--to', (Q $p2), (Q $smoke))
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    if ($ask) { Press (Find-Id $ask '6') }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $p2 'Assets\VFX\fumo.png')) -eq 'S2') 'invio rapido verso il progetto aperto'
    Note ('dal log: ' + (((Log-Since $mark) -split "`n" | Where-Object { $_ -match 'primo piano|finestra di Unity' }) -join ' | '))
    Check ([Win]::GetForegroundWindow() -eq $fakeHandle) "Unity portato in primo piano anche con l'invio rapido"
    try { $other.Kill() } catch { }

    Write-Host '--- 8. sposta, dalla voce "Scegli progetto e cartella..."'
    $moving = New-File (Join-Path $in 'da_spostare.png') 'M'
    $proc = Start-Tool @('--pick', (Q $moving))
    $dialog = Find-Window $proc.Id 'Invia a Unity' 'WindowsForms10*'
    if ($dialog) {
        Save-Shot $dialog '6-finestra-scegli'
        Toggle (Find-Id $dialog 'moveCheck')
        Set-Text (Find-Id $dialog 'folderCombo') 'Assets/Spostati'
        Press (Find-Id $dialog 'sendButton')
    }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $p2 'Assets\Spostati\da_spostare.png')) -eq 'M') 'file spostato nel progetto aperto (proposto per primo)'
    Check (-not (Exists $moving)) "spostando, l'originale sparisce"
    try { $fake.Kill() } catch { }

    Write-Host '--- 9. esperimento: piu di 15 file dal menu (solo informativo)'
    function Try-Verb([string]$name, [string]$model, [int]$count) {
        $folder = Join-Path $work "esperimento-$name"
        $files = @(1..$count | ForEach-Object { [IO.Path]::GetFileName((New-File (Join-Path $folder "$name-$_.png") 'E')) })
        $verbKey = $registry.CreateSubKey("Software\Classes\*\shell\STU$name")
        if ($model) { $verbKey.SetValue('MultiSelectModel', $model) }
        $commandKey = $verbKey.CreateSubKey('command')
        $commandKey.SetValue('', (Q $exe) + ' --to ' + (Q $p1) + ' "%1"')
        $commandKey.Close()
        $verbKey.Close()
        try {
            $items = (New-Object -ComObject Shell.Application).NameSpace($folder).Items()
            $items.InvokeVerbEx("STU$name")
        } catch { Note "InvokeVerbEx: $($_.Exception.Message)" }
        Start-Sleep -Seconds 12
        Get-Process SendToUnity -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
        $registry.DeleteSubKeyTree("Software\Classes\*\shell\STU$name", $false)
        @($files | Where-Object { Exists (Join-Path $dest $_) }).Count
    }
    Note ("10 file, modello predefinito (Document): arrivati " + (Try-Verb 'doc10' $null 10))
    Note ("20 file, modello predefinito (Document): arrivati " + (Try-Verb 'doc20' $null 20))
    Note ("20 file, MultiSelectModel=Player: arrivati " + (Try-Verb 'player20' 'Player' 20))

    Write-Host '--- 10. disinstallazione'
    Copy-Item -LiteralPath $log -Destination $savedLog -ErrorAction SilentlyContinue
    Run-Native { & (Join-Path $src 'uninstall.cmd') /quiet }
    Check ($null -eq $registry.OpenSubKey($fileMenu)) 'voce sui file rimossa'
    Check ($null -eq $registry.OpenSubKey($dirMenu)) 'voce sulle cartelle rimossa'
    Check (-not (Exists $sendToLink)) 'collegamento in "Invia a" rimosso'
    Check (-not (Exists $app)) 'cartella del programma rimossa'
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
