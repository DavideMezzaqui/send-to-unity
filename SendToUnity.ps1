# SendToUnity: copia i file selezionati in un progetto Unity (aperto o chiuso).
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Paths)

$ErrorActionPreference = 'Stop'

$AppDir     = Join-Path $env:LOCALAPPDATA 'SendToUnity'
$ConfigFile = Join-Path $AppDir 'config.json'
$QueueDir   = Join-Path $env:TEMP 'SendToUnity-queue'
$Title      = 'Invia a Unity'

# ---------------------------------------------------------------- raccolta file
# launcher.vbs scrive ogni file selezionato in una coda e crea leader.lock prima
# di avviare PowerShell. Qui si aspetta che la coda smetta di crescere, si prende
# tutto e si toglie il lock; poi si ricontrolla la coda, cosi' un file arrivato
# mentre il lock esisteva ancora non resta orfano. Il mutex evita che due
# PowerShell leggano la stessa coda insieme.
function Read-Queue($into) {
    $cutoff = (Get-Date).AddMinutes(-2)
    foreach ($f in [IO.Directory]::GetFiles($QueueDir, '*.txt')) {
        # i file vecchi sono residui di esecuzioni interrotte: si scartano
        if ([IO.File]::GetLastWriteTime($f) -gt $cutoff) {
            try { $into.AddRange([IO.File]::ReadAllLines($f)) } catch { continue }
        }
        [IO.File]::Delete($f)
    }
    foreach ($f in [IO.Directory]::GetFiles($QueueDir, '*.tmp')) {
        if ([IO.File]::GetLastWriteTime($f) -lt $cutoff) { [IO.File]::Delete($f) }
    }
}

function Collect-Files([string[]]$Initial) {
    [void][IO.Directory]::CreateDirectory($QueueDir)

    # Avvio diretto con i percorsi come argomenti (senza launcher.vbs)
    $Initial = @($Initial | Where-Object { $_ })
    if ($Initial.Count -gt 0) {
        $tmp = Join-Path $QueueDir "$([guid]::NewGuid()).tmp"
        [IO.File]::WriteAllLines($tmp, [string[]]$Initial, [Text.Encoding]::UTF8)
        [IO.File]::Move($tmp, [IO.Path]::ChangeExtension($tmp, '.txt'))
    }

    $all = New-Object System.Collections.Generic.List[string]
    $mutex = [Threading.Mutex]::new($false, 'Local\SendToUnityQueue')
    try {
        try { [void]$mutex.WaitOne() } catch [Threading.AbandonedMutexException] { }

        $last = -1; $stable = 0
        do {
            Start-Sleep -Milliseconds 250
            $count = [IO.Directory]::GetFiles($QueueDir, '*.txt').Count
            if ($count -eq $last) { $stable++ } else { $stable = 0; $last = $count }
        } while ($stable -lt 2)

        Read-Queue $all
        [IO.File]::Delete((Join-Path $QueueDir 'leader.lock'))
        Read-Queue $all
    } finally {
        try { $mutex.ReleaseMutex() } catch { }
        $mutex.Dispose()
    }
    return @($all | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)
}

# ---------------------------------------------------------------- configurazione
function Load-Config {
    $c = @{ lastProject = $null; subfolders = @{}; extraProjects = @() }
    if (Test-Path -LiteralPath $ConfigFile) {
        try {
            $j = Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.lastProject) { $c.lastProject = [string]$j.lastProject }
            if ($j.subfolders) {
                foreach ($p in $j.subfolders.PSObject.Properties) { $c.subfolders[$p.Name] = [string]$p.Value }
            }
            if ($j.extraProjects) { $c.extraProjects = @($j.extraProjects) }
        } catch { }
    }
    return $c
}

function Save-Config($c) {
    [void][IO.Directory]::CreateDirectory($AppDir)
    $c | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ConfigFile -Encoding UTF8
}

# ---------------------------------------------------------------- progetti Unity
function Normalize-Path([string]$p) {
    return [IO.Path]::GetFullPath($p.Trim().Trim('"')).TrimEnd('\', '/')
}

function Test-UnityProject([string]$p) {
    return (Test-Path -LiteralPath (Join-Path $p 'Assets') -PathType Container) -and
           (Test-Path -LiteralPath (Join-Path $p 'ProjectSettings') -PathType Container)
}

# Editor aperti con -projectPath sulla riga di comando (avvio da Unity Hub)
function Get-RunningEditors {
    $res = @{}
    try { $procs = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'") } catch { $procs = @() }
    foreach ($p in $procs) {
        if ($p.CommandLine -match '-projectpath\s+(?:"([^"]+)"|(\S+))') {
            $path = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
            try { $n = Normalize-Path $path } catch { continue }
            $res[$n.ToLowerInvariant()] = @{ Path = $n; ProcId = [int]$p.ProcessId }
        }
    }
    return $res
}

# Un editor aperto tiene bloccato Temp\UnityLockfile: copre anche i casi senza -projectPath
function Test-ProjectLocked([string]$p) {
    $lock = Join-Path $p 'Temp\UnityLockfile'
    if (-not [IO.File]::Exists($lock)) { return $false }
    try {
        $fs = [IO.File]::Open($lock, 'Open', 'ReadWrite', 'None')
        $fs.Dispose()
        return $false
    } catch { return $true }
}

# Progetti conosciuti: Unity Hub + progetti recenti salvati dall'editor nel registro
function Get-KnownProjectPaths {
    $paths = New-Object System.Collections.Generic.List[string]

    $hub = Join-Path $env:APPDATA 'UnityHub\projects-v1.json'
    if (Test-Path -LiteralPath $hub) {
        try {
            $j = Get-Content -LiteralPath $hub -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.data) {
                foreach ($prop in $j.data.PSObject.Properties) {
                    $v = $prop.Value
                    $paths.Add($(if ($v.path) { [string]$v.path } else { [string]$prop.Name }))
                }
            }
        } catch { }
    }

    $base = 'HKCU:\Software\Unity Technologies'
    if (Test-Path $base) {
        foreach ($k in @(Get-ChildItem $base -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'Unity Editor*' })) {
            foreach ($name in $k.GetValueNames()) {
                if ($name -notlike 'RecentlyUsedProjectPaths*') { continue }
                $v = $k.GetValue($name)
                if ($v -is [byte[]]) { $v = [Text.Encoding]::UTF8.GetString($v) }
                if ($v) { $paths.Add(([string]$v).TrimEnd([char]0)) }
            }
        }
    }
    return $paths
}

function Build-Entries($cfg) {
    $running = Get-RunningEditors
    $known = @{}
    $runningPaths = @($running.Values | ForEach-Object { $_.Path })
    foreach ($p in @(Get-KnownProjectPaths) + @($cfg.extraProjects) + $runningPaths) {
        if (-not $p) { continue }
        try { $n = Normalize-Path $p } catch { continue }
        $k = $n.ToLowerInvariant()
        if (-not $known.ContainsKey($k) -and (Test-UnityProject $n)) { $known[$k] = $n }
    }

    $list = foreach ($k in $known.Keys) {
        $path = $known[$k]
        $procId = if ($running.ContainsKey($k)) { $running[$k].ProcId } else { 0 }
        $isOpen = ($procId -ne 0) -or (Test-ProjectLocked $path)
        $name = Split-Path $path -Leaf
        [pscustomobject]@{
            Path   = $path
            Name   = $name
            Open   = $isOpen
            ProcId = $procId
            Label  = $(if ($isOpen) { "[aperto]  $name   ($path)" } else { "$name   ($path)" })
            Sort   = $(if ($isOpen) { "0$name" } else { "1$name" })
        }
    }
    return @($list | Sort-Object Sort)
}

function Get-Subfolders([string]$proj) {
    $list = New-Object System.Collections.Generic.List[string]
    $list.Add('Assets')
    $assets = Join-Path $proj 'Assets'
    foreach ($d in @(Get-ChildItem -LiteralPath $assets -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue)) {
        $list.Add(($d.FullName.Substring($proj.Length + 1) -replace '\\', '/'))
    }
    return $list
}

# Ripulisce e controlla la cartella scritta dall'utente. Ritorna $null se non valida.
function Resolve-Subfolder([string]$text) {
    $s = ($text -replace '\\', '/').Trim().Trim('/')
    if ($s -eq '') { $s = 'Assets' }
    $parts = @($s -split '/+')
    if ($parts[0] -ne 'Assets') { return $null }       # -ne non distingue maiuscole
    $parts[0] = 'Assets'
    $bad = [IO.Path]::GetInvalidFileNameChars()
    foreach ($part in $parts) {
        if ($part -eq '.' -or $part -eq '..' -or $part.IndexOfAny($bad) -ge 0) { return $null }
    }
    return ($parts -join '/')
}

function Focus-Unity($entry) {
    if (-not ('SendToUnityWin' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SendToUnityWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@
    }
    $p = $null
    if ($entry.ProcId) { $p = Get-Process -Id $entry.ProcId -ErrorAction SilentlyContinue }
    if (-not $p -or $p.MainWindowHandle -eq [IntPtr]::Zero) {
        # Il titolo della finestra di Unity inizia con il nome del progetto
        $p = Get-Process -Name Unity -ErrorAction SilentlyContinue |
             Where-Object { $_.MainWindowTitle -like "$($entry.Name) - *" } | Select-Object -First 1
    }
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
        $h = $p.MainWindowHandle
        if ([SendToUnityWin]::IsIconic($h)) { [void][SendToUnityWin]::ShowWindowAsync($h, 9) }
        [void][SendToUnityWin]::SetForegroundWindow($h)
    }
}

function Send-Item([string]$src, [string]$target, [bool]$move, [bool]$overwrite) {
    if ([IO.Directory]::Exists($src)) {
        # robocopy unisce le cartelle invece di annidarle come fa Copy-Item
        $rcArgs = @($src, $target, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
        if ($move) { $rcArgs += '/MOVE' }
        if (-not $overwrite) { $rcArgs += @('/XC', '/XN', '/XO') }
        & robocopy.exe @rcArgs | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copia della cartella non riuscita: $src" }
    } elseif ($move) {
        Move-Item -LiteralPath $src -Destination $target -Force
    } else {
        Copy-Item -LiteralPath $src -Destination $target -Force
    }
}

# ---------------------------------------------------------------- finestra
# Messaggio con un proprietario "sempre in primo piano" (mai mostrato):
# senza, dopo la chiusura della finestra principale potrebbe finire dietro Explorer.
function Show-Message([string]$text, [string]$buttons = 'OK', [string]$icon = 'Information') {
    $owner = New-Object System.Windows.Forms.Form
    $owner.TopMost = $true
    try { return [System.Windows.Forms.MessageBox]::Show($owner, $text, $Title, $buttons, $icon) }
    finally { $owner.Dispose() }
}

function Show-Dialog($files, $entries, $cfg) {
    $items = New-Object System.Collections.ArrayList
    foreach ($e in $entries) { [void]$items.Add($e) }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $Title
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false; $form.MinimizeBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(560, 240)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Location = New-Object System.Drawing.Point(12, 12)
    $lbl.Size = New-Object System.Drawing.Size(536, 20)
    $first = [IO.Path]::GetFileName($files[0])
    $lbl.Text = if ($files.Count -eq 1) { "File: $first" } else { "File da inviare: $($files.Count)  ($first, ...)" }
    $form.Controls.Add($lbl)

    $l1 = New-Object System.Windows.Forms.Label
    $l1.Text = 'Progetto'; $l1.AutoSize = $true
    $l1.Location = New-Object System.Drawing.Point(12, 42)
    $form.Controls.Add($l1)

    $cmbProj = New-Object System.Windows.Forms.ComboBox
    $cmbProj.DropDownStyle = 'DropDownList'
    $cmbProj.Location = New-Object System.Drawing.Point(12, 62)
    $cmbProj.Size = New-Object System.Drawing.Size(440, 24)
    foreach ($e in $items) { [void]$cmbProj.Items.Add($e.Label) }
    $form.Controls.Add($cmbProj)

    $btnAdd = New-Object System.Windows.Forms.Button
    $btnAdd.Text = 'Aggiungi...'
    $btnAdd.Location = New-Object System.Drawing.Point(460, 60)
    $btnAdd.Size = New-Object System.Drawing.Size(88, 27)
    $form.Controls.Add($btnAdd)

    $l2 = New-Object System.Windows.Forms.Label
    $l2.Text = 'Cartella di destinazione (dentro Assets, puoi scriverne una nuova)'; $l2.AutoSize = $true
    $l2.Location = New-Object System.Drawing.Point(12, 98)
    $form.Controls.Add($l2)

    $cmbDir = New-Object System.Windows.Forms.ComboBox
    $cmbDir.DropDownStyle = 'DropDown'
    $cmbDir.Location = New-Object System.Drawing.Point(12, 118)
    $cmbDir.Size = New-Object System.Drawing.Size(536, 24)
    $form.Controls.Add($cmbDir)

    $chkMove = New-Object System.Windows.Forms.CheckBox
    $chkMove.Text = 'Sposta invece di copiare'; $chkMove.AutoSize = $true
    $chkMove.Location = New-Object System.Drawing.Point(12, 154)
    $form.Controls.Add($chkMove)

    $chkFocus = New-Object System.Windows.Forms.CheckBox
    $chkFocus.Text = 'Porta Unity in primo piano (se aperto)'; $chkFocus.AutoSize = $true
    $chkFocus.Location = New-Object System.Drawing.Point(12, 178)
    $chkFocus.Checked = $true
    $form.Controls.Add($chkFocus)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'Invia'
    $ok.Location = New-Object System.Drawing.Point(372, 200)
    $ok.Size = New-Object System.Drawing.Size(88, 30)
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.Add($ok); $form.AcceptButton = $ok

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Annulla'
    $cancel.Location = New-Object System.Drawing.Point(466, 200)
    $cancel.Size = New-Object System.Drawing.Size(82, 30)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancel); $form.CancelButton = $cancel

    $cmbProj.Add_SelectedIndexChanged({
        $cmbDir.Items.Clear()
        $i = $cmbProj.SelectedIndex
        if ($i -lt 0) { return }
        $e = $items[$i]
        foreach ($d in Get-Subfolders $e.Path) { [void]$cmbDir.Items.Add($d) }
        $saved = $cfg.subfolders[$e.Path]
        $cmbDir.Text = if ($saved) { $saved } else { 'Assets' }
    })

    $btnAdd.Add_Click({
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = 'Scegli la cartella principale di un progetto Unity (quella che contiene Assets)'
        if ($dlg.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK) { return }
        $n = Normalize-Path $dlg.SelectedPath
        if (-not (Test-UnityProject $n)) {
            [void][System.Windows.Forms.MessageBox]::Show($form, 'Questa cartella non sembra un progetto Unity (mancano Assets o ProjectSettings).', $Title)
            return
        }
        for ($i = 0; $i -lt $items.Count; $i++) {
            if ($items[$i].Path -eq $n) { $cmbProj.SelectedIndex = $i; return }
        }
        $cfg.extraProjects = @($cfg.extraProjects) + $n
        Save-Config $cfg
        $name = Split-Path $n -Leaf
        $e = [pscustomobject]@{ Path = $n; Name = $name; Open = $false; ProcId = 0; Label = "$name   ($n)"; Sort = "1$name" }
        [void]$items.Add($e)
        $cmbProj.SelectedIndex = $cmbProj.Items.Add($e.Label)
    })

    $form.Add_Shown({ $form.Activate() })

    if ($items.Count -gt 0) {
        $sel = 0
        for ($i = 0; $i -lt $items.Count; $i++) {
            if ($cfg.lastProject -and $items[$i].Path -eq $cfg.lastProject) { $sel = $i; break }
        }
        $cmbProj.SelectedIndex = $sel
    }

    # Controllo prima che la finestra si chiuda: se qualcosa non va resta aperta
    $form.Add_FormClosing({
        param($s, $ev)
        if ($form.DialogResult -ne [System.Windows.Forms.DialogResult]::OK) { return }
        $msg = $null
        if ($cmbProj.SelectedIndex -lt 0) {
            $msg = 'Scegli un progetto, oppure aggiungilo con "Aggiungi...".'
        } elseif (-not (Resolve-Subfolder $cmbDir.Text)) {
            $msg = "La cartella deve stare dentro Assets (es. Assets/Textures) e non puo' contenere caratteri non validi."
        }
        if ($msg) {
            [void][System.Windows.Forms.MessageBox]::Show($form, $msg, $Title)
            $ev.Cancel = $true
        }
    })

    try {
        if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
        return @{
            Project   = $items[$cmbProj.SelectedIndex]
            Subfolder = Resolve-Subfolder $cmbDir.Text
            Move      = $chkMove.Checked
            Focus     = $chkFocus.Checked
        }
    } finally { $form.Dispose() }
}

# ---------------------------------------------------------------- main
function Main {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $files = @(Collect-Files $Paths)
    if ($files.Count -eq 0) { return }

    $cfg = Load-Config
    $entries = @(Build-Entries $cfg)
    $r = Show-Dialog $files $entries $cfg
    if (-not $r) { return }

    $proj = $r.Project.Path
    $dest = Join-Path $proj ($r.Subfolder -replace '/', '\')
    [void][IO.Directory]::CreateDirectory($dest)

    $seen = @{}   # due file selezionati con lo stesso nome finirebbero uno sull'altro
    $plan = foreach ($f in $files) {
        $full = Normalize-Path $f
        $target = Join-Path $dest ([IO.Path]::GetFileName($full))
        if ($full -eq $target) { continue }   # e' gia' li'
        if ($target.StartsWith("$full\", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Non posso copiare la cartella '$full' dentro se stessa."
        }
        $dup = $seen.ContainsKey($target)
        $seen[$target] = $true
        @{ Src = $full; Target = $target; Exists = ($dup -or (Test-Path -LiteralPath $target)) }
    }
    $plan = @($plan)
    if ($plan.Count -eq 0) { return }

    $existing = @($plan | Where-Object { $_.Exists }).Count
    $overwrite = $true
    if ($existing -gt 0) {
        $ans = Show-Message "$existing elementi esistono gia' nella destinazione (o hanno lo stesso nome tra quelli selezionati).`n`nSi' = sovrascrivi`nNo = salta quelli esistenti`nAnnulla = non inviare niente" 'YesNoCancel' 'Question'
        if ($ans -eq [System.Windows.Forms.DialogResult]::Cancel) { return }
        $overwrite = ($ans -eq [System.Windows.Forms.DialogResult]::Yes)
    }

    $done = 0; $skipped = 0; $errors = @()
    foreach ($item in $plan) {
        if ($item.Exists -and -not $overwrite -and -not [IO.Directory]::Exists($item.Src)) { $skipped++; continue }
        try {
            Send-Item $item.Src $item.Target $r.Move $overwrite
            $done++
        } catch {
            $errors += "$([IO.Path]::GetFileName($item.Src)): $($_.Exception.Message)"
        }
    }

    $cfg.lastProject = $proj
    $cfg.subfolders[$proj] = $r.Subfolder
    Save-Config $cfg

    if ($errors.Count -gt 0) {
        $msg = "Inviati: $done. Saltati: $skipped. Errori: $($errors.Count)`n`n" + (($errors | Select-Object -First 10) -join "`n")
        [void](Show-Message $msg 'OK' 'Warning')
    } elseif ($skipped -gt 0) {
        [void](Show-Message "Inviati: $done. Saltati: $skipped.")
    }

    # Dopo i messaggi, altrimenti finirebbero dietro Unity
    if ($r.Focus -and $r.Project.Open) {
        try { Focus-Unity $r.Project } catch { }   # se non riesce, i file sono comunque copiati
    }
}

if ($env:SENDTOUNITY_NO_MAIN) { return }   # usato dai test per caricare solo le funzioni
try { Main }
catch {
    try { [void](Show-Message $_.Exception.Message 'OK' 'Error') } catch { }
}
