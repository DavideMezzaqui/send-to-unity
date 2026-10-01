# SendToUnity: copia i file selezionati in un progetto Unity (aperto o chiuso).
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Paths)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$AppDir     = Join-Path $env:LOCALAPPDATA 'SendToUnity'
$ConfigFile = Join-Path $AppDir 'config.json'
$QueueDir   = Join-Path $env:TEMP 'SendToUnity-queue'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SendToUnityWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@

# ---------------------------------------------------------------- raccolta file
# Explorer avvia una istanza per ogni file selezionato (voce del tasto destro).
# La prima istanza diventa "leader", aspetta che arrivino le altre e le unisce.
function Collect-Files([string[]]$Initial) {
    New-Item -ItemType Directory -Force -Path $QueueDir | Out-Null
    $id  = [guid]::NewGuid().ToString()
    $tmp = Join-Path $QueueDir "$id.tmp"
    [IO.File]::WriteAllLines($tmp, $Initial, [Text.Encoding]::UTF8)
    Move-Item -LiteralPath $tmp -Destination (Join-Path $QueueDir "$id.txt")

    $created = $false
    $script:LeaderMutex = New-Object System.Threading.Mutex($true, 'Local\SendToUnityLeader', [ref]$created)
    if (-not $created) { return $null }   # un'altra istanza gestisce tutto

    $last = -1; $stable = 0
    do {
        Start-Sleep -Milliseconds 300
        $count = @(Get-ChildItem -LiteralPath $QueueDir -Filter *.txt).Count
        if ($count -eq $last) { $stable++ } else { $stable = 0; $last = $count }
    } while ($stable -lt 2)

    $all = @()
    foreach ($f in Get-ChildItem -LiteralPath $QueueDir -Filter *.txt) {
        $all += [IO.File]::ReadAllLines($f.FullName, [Text.Encoding]::UTF8)
        Remove-Item -LiteralPath $f.FullName -Force
    }
    return @($all | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)
}

# ---------------------------------------------------------------- configurazione
function Load-Config {
    $c = @{ lastProject = $null; subfolders = @{}; extraProjects = @() }
    if (Test-Path $ConfigFile) {
        try {
            $j = Get-Content $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.lastProject) { $c.lastProject = $j.lastProject }
            if ($j.subfolders) {
                foreach ($p in $j.subfolders.PSObject.Properties) { $c.subfolders[$p.Name] = $p.Value }
            }
            if ($j.extraProjects) { $c.extraProjects = @($j.extraProjects) }
        } catch { }
    }
    return $c
}
function Save-Config($c) {
    New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
    $c | ConvertTo-Json -Depth 5 | Set-Content -Path $ConfigFile -Encoding UTF8
}

# ---------------------------------------------------------------- progetti Unity
function Normalize-Path([string]$p) {
    return [IO.Path]::GetFullPath($p.Trim('"')).TrimEnd('\')
}

# Editor aperti: Unity.exe viene avviato con -projectpath <cartella>
function Get-OpenProjects {
    $res = @{}
    $procs = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        if ($p.CommandLine -match '-projectpath\s+(?:"([^"]+)"|(\S+))') {
            $path = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
            $res[(Normalize-Path $path).ToLowerInvariant()] = @{ Path = (Normalize-Path $path); ProcId = $p.ProcessId }
        }
    }
    return $res
}

# Progetti noti a Unity Hub (anche chiusi)
function Get-HubProjects {
    $f = Join-Path $env:APPDATA 'UnityHub\projects-v1.json'
    $paths = @()
    if (Test-Path $f) {
        try {
            $j = Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.data) {
                foreach ($prop in $j.data.PSObject.Properties) {
                    $v = $prop.Value
                    $paths += $(if ($v.path) { $v.path } else { $prop.Name })
                }
            }
        } catch { }
    }
    return $paths
}

function Build-Entries($cfg) {
    $open = Get-OpenProjects
    $known = @{}
    foreach ($k in $open.Keys) { $known[$k] = $open[$k].Path }
    foreach ($p in @(Get-HubProjects) + @($cfg.extraProjects)) {
        if (-not $p) { continue }
        try { $n = Normalize-Path $p } catch { continue }
        if (Test-Path (Join-Path $n 'Assets')) { $known[$n.ToLowerInvariant()] = $n }
    }
    $entries = foreach ($k in $known.Keys) {
        $isOpen = $open.ContainsKey($k)
        $name = Split-Path $known[$k] -Leaf
        [pscustomobject]@{
            Path   = $known[$k]
            Open   = $isOpen
            ProcId = $(if ($isOpen) { $open[$k].ProcId } else { 0 })
            Label  = $(if ($isOpen) { "[aperto]  $name" } else { "$name" }) + "   ($($known[$k]))"
            Sort   = $(if ($isOpen) { "0$name" } else { "1$name" })
        }
    }
    return @($entries | Sort-Object Sort)
}

function Get-Subfolders([string]$proj) {
    $list = @('Assets')
    $assets = Join-Path $proj 'Assets'
    if (Test-Path $assets) {
        Get-ChildItem -LiteralPath $assets -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue |
            ForEach-Object { $list += ($_.FullName.Substring($proj.Length + 1) -replace '\\', '/') }
    }
    return $list
}

function Focus-Unity([int]$ProcId) {
    $p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
        if ([SendToUnityWin]::IsIconic($p.MainWindowHandle)) { [SendToUnityWin]::ShowWindowAsync($p.MainWindowHandle, 9) | Out-Null }
        [SendToUnityWin]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    }
}

# ---------------------------------------------------------------- finestra
function Show-Dialog($files, $entries, $cfg) {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Invia a Unity'
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false; $form.MinimizeBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(560, 235)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Location = '12,12'; $lbl.Size = '536,20'
    $lbl.Text = "File da inviare: $($files.Count)"
    $form.Controls.Add($lbl)

    $l1 = New-Object System.Windows.Forms.Label
    $l1.Text = 'Progetto'; $l1.Location = '12,42'; $l1.AutoSize = $true
    $form.Controls.Add($l1)

    $cmbProj = New-Object System.Windows.Forms.ComboBox
    $cmbProj.DropDownStyle = 'DropDownList'
    $cmbProj.Location = '12,62'; $cmbProj.Size = '440,24'
    $cmbProj.DisplayMember = 'Label'
    foreach ($e in $entries) { [void]$cmbProj.Items.Add($e) }
    $form.Controls.Add($cmbProj)

    $btnAdd = New-Object System.Windows.Forms.Button
    $btnAdd.Text = 'Aggiungi...'; $btnAdd.Location = '460,60'; $btnAdd.Size = '88,27'
    $form.Controls.Add($btnAdd)

    $l2 = New-Object System.Windows.Forms.Label
    $l2.Text = 'Cartella di destinazione (dentro il progetto)'; $l2.Location = '12,98'; $l2.AutoSize = $true
    $form.Controls.Add($l2)

    $cmbDir = New-Object System.Windows.Forms.ComboBox
    $cmbDir.DropDownStyle = 'DropDown'
    $cmbDir.Location = '12,118'; $cmbDir.Size = '536,24'
    $form.Controls.Add($cmbDir)

    $chkMove = New-Object System.Windows.Forms.CheckBox
    $chkMove.Text = 'Sposta invece di copiare'; $chkMove.Location = '12,152'; $chkMove.AutoSize = $true
    $form.Controls.Add($chkMove)

    $chkFocus = New-Object System.Windows.Forms.CheckBox
    $chkFocus.Text = 'Porta Unity in primo piano (se aperto)'; $chkFocus.Location = '12,176'; $chkFocus.AutoSize = $true
    $chkFocus.Checked = $true
    $form.Controls.Add($chkFocus)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'Invia'; $ok.Location = '372,195'; $ok.Size = '88,30'
    $ok.DialogResult = 'OK'
    $form.Controls.Add($ok); $form.AcceptButton = $ok

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Annulla'; $cancel.Location = '466,195'; $cancel.Size = '82,30'
    $cancel.DialogResult = 'Cancel'
    $form.Controls.Add($cancel); $form.CancelButton = $cancel

    $refreshDirs = {
        $e = $cmbProj.SelectedItem
        $cmbDir.Items.Clear()
        if (-not $e) { return }
        foreach ($d in Get-Subfolders $e.Path) { [void]$cmbDir.Items.Add($d) }
        $saved = $cfg.subfolders[$e.Path]
        $cmbDir.Text = $(if ($saved) { $saved } else { 'Assets' })
    }
    $cmbProj.Add_SelectedIndexChanged($refreshDirs)

    $btnAdd.Add_Click({
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = 'Scegli la cartella radice di un progetto Unity (quella che contiene Assets)'
        if ($dlg.ShowDialog($form) -eq 'OK') {
            if (-not (Test-Path (Join-Path $dlg.SelectedPath 'Assets'))) {
                [void][System.Windows.Forms.MessageBox]::Show('Questa cartella non contiene Assets.', 'Invia a Unity')
                return
            }
            $n = Normalize-Path $dlg.SelectedPath
            $cfg.extraProjects = @($cfg.extraProjects) + $n
            Save-Config $cfg
            $e = [pscustomobject]@{ Path = $n; Open = $false; ProcId = 0; Label = "$(Split-Path $n -Leaf)   ($n)"; Sort = '1' }
            $idx = $cmbProj.Items.Add($e)
            $cmbProj.SelectedIndex = $idx
        }
    })

    if ($cmbProj.Items.Count -gt 0) {
        $sel = 0
        for ($i = 0; $i -lt $cmbProj.Items.Count; $i++) {
            if ($cfg.lastProject -and $cmbProj.Items[$i].Path -eq $cfg.lastProject) { $sel = $i; break }
        }
        $cmbProj.SelectedIndex = $sel
    }

    if ($form.ShowDialog() -ne 'OK') { return $null }
    $e = $cmbProj.SelectedItem
    if (-not $e) {
        [void][System.Windows.Forms.MessageBox]::Show('Nessun progetto selezionato.', 'Invia a Unity')
        return $null
    }
    return @{ Project = $e; Subfolder = $cmbDir.Text.Trim().Replace('\', '/').Trim('/'); Move = $chkMove.Checked; Focus = $chkFocus.Checked }
}

# ---------------------------------------------------------------- main
try {
    $files = Collect-Files $Paths
    if (-not $files -or $files.Count -eq 0) { exit }

    $cfg = Load-Config
    $entries = Build-Entries $cfg
    if ($entries.Count -eq 0) {
        [void][System.Windows.Forms.MessageBox]::Show("Non trovo nessun progetto Unity.`nApri un progetto o usa 'Aggiungi...' nella finestra.", 'Invia a Unity')
    }
    $r = Show-Dialog $files $entries $cfg
    if (-not $r) { exit }

    $sub = $r.Subfolder
    if ($sub -ne 'Assets' -and -not $sub.StartsWith('Assets/') -or $sub -match '(^|/)\.\.(/|$)') {
        [void][System.Windows.Forms.MessageBox]::Show("La cartella deve stare dentro Assets (es. Assets/Textures).", 'Invia a Unity')
        exit
    }

    $proj = $r.Project.Path
    $dest = Join-Path $proj $sub
    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    $existing = @($files | Where-Object { Test-Path -LiteralPath (Join-Path $dest (Split-Path $_ -Leaf)) })
    $overwrite = $true
    if ($existing.Count -gt 0) {
        $ans = [System.Windows.Forms.MessageBox]::Show("$($existing.Count) file esistono già nella destinazione.`nSì = sovrascrivi, No = salta, Annulla = interrompi.", 'Invia a Unity', 'YesNoCancel', 'Question')
        if ($ans -eq 'Cancel') { exit }
        $overwrite = ($ans -eq 'Yes')
    }

    $done = 0; $skipped = 0
    foreach ($f in $files) {
        $target = Join-Path $dest (Split-Path $f -Leaf)
        if ((Test-Path -LiteralPath $target) -and -not $overwrite) { $skipped++; continue }
        if ($r.Move) { Move-Item -LiteralPath $f -Destination $target -Force }
        else         { Copy-Item -LiteralPath $f -Destination $target -Recurse -Force }
        $done++
    }

    $cfg.lastProject = $proj
    $cfg.subfolders[$proj] = $sub
    Save-Config $cfg

    if ($r.Focus -and $r.Project.Open) { Focus-Unity $r.Project.ProcId }
    if ($skipped -gt 0) {
        [void][System.Windows.Forms.MessageBox]::Show("Inviati: $done. Saltati: $skipped.", 'Invia a Unity')
    }
}
catch {
    [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Invia a Unity: errore')
}
