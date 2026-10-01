# Diagnostica (solo GitHub Actions): il menu del tasto destro lancia comandi senza clic?
# Registra un sottomenu con la stessa struttura di "Invia a Unity" (comandi che scrivono
# una riga in un file), poi lo legge come fa Explorer, con file .txt e .png, e infine
# apre il menu in una vera finestra di Esplora risorse e lo chiude con Esc.

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { Write-Host 'Solo su GitHub Actions.'; exit 2 }

Add-Type -Path (Join-Path $PSScriptRoot 'WinTest.cs')
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$registry = [Microsoft.Win32.Registry]::CurrentUser
$work     = Join-Path $env:RUNNER_TEMP 'diag'
$marker   = Join-Path $work 'invocazioni.txt'
$shots    = Join-Path $env:RUNNER_TEMP 'stu-shots'
$keys     = @('Software\Classes\*\shell\STUDiag', 'Software\Classes\Directory\shell\STUDiag')

function Q([string]$text) { '"' + $text + '"' }
function Calls([int]$seconds) {
    Start-Sleep -Seconds $seconds
    if ([IO.File]::Exists($marker)) { @([IO.File]::ReadAllLines($marker) | Where-Object { $_.Trim() }) } else { @() }
}

[void][IO.Directory]::CreateDirectory($work)
$txt = Join-Path $work 'prova.txt'
$png = Join-Path $work 'prova.png'
[IO.File]::WriteAllText($txt, 'x')
# un vero PNG 1x1, cosi' Windows aggiunge le voci per le immagini
[IO.File]::WriteAllBytes($png, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='))

# stessa struttura del programma: Player, due progetti, separatore, "Scegli..."
foreach ($keyPath in $keys) {
    $registry.DeleteSubKeyTree($keyPath, $false)
    $key = $registry.CreateSubKey($keyPath)
    $key.SetValue('MUIVerb', 'Diagnosi Unity')
    $key.SetValue('SubCommands', '')
    $key.SetValue('MultiSelectModel', 'Player')
    $shell = $key.CreateSubKey('shell')
    foreach ($entry in @(@('00', 'Voce uno', $false), @('01', 'Voce due', $false), @('99', 'Scegli...', $true))) {
        $item = $shell.CreateSubKey($entry[0])
        $item.SetValue('MUIVerb', $entry[1])
        $item.SetValue('MultiSelectModel', 'Player')
        if ($entry[2]) { $item.SetValue('CommandFlags', 0x20, [Microsoft.Win32.RegistryValueKind]::DWord) }
        $item.CreateSubKey('command').SetValue('', (Q "$env:WINDIR\System32\cmd.exe") + ' /c echo ' + $entry[1] + ' %1>>' + (Q $marker))
        $item.Close()
    }
    $shell.Close()
    $key.Close()
}

foreach ($file in @($txt, $png)) {
    foreach ($how in @('Submenu', 'Steps')) {
        Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
        try {
            if ($how -eq 'Submenu') {
                $items = [ExplorerMenu]::Submenu([string[]]@($file), 'Diagnosi Unity', $null)
                $report = [ExplorerMenu]::Info + ' | voci: ' + (@($items) -join ' / ')
            } else {
                $report = [ExplorerMenu]::Steps([string[]]@($file), 'Diagnosi Unity', 3, $null)
            }
            $calls = Calls 5
            Write-Host ("DIAG lettura {0} con {1} | {2} | partenze: {3} {4}" -f [IO.Path]::GetExtension($file), $how, $report, $calls.Count, ($calls -join '; '))
        } catch {
            Write-Host ("DIAG lettura {0} con {1} | errore: {2}" -f [IO.Path]::GetExtension($file), $how, $_.Exception.Message)
        }
    }
}

# Vera finestra di Esplora risorse: Maiusc+F10 apre il menu classico, Esc lo chiude senza scegliere
foreach ($file in @($png, $txt)) {
    Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
    try {
        Start-Process explorer.exe -ArgumentList ('/select,' + (Q $file))
        Start-Sleep -Seconds 4
        $shell = New-Object -ComObject Shell.Application
        $window = @($shell.Windows()) | Where-Object { $_.Document -and $_.Document.Folder.Self.Path -eq $work } | Select-Object -First 1
        if ($window) {
            [void][Win]::SetForegroundWindow([IntPtr]$window.HWND)
            Start-Sleep -Milliseconds 800
            [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
            Start-Sleep -Seconds 2
            $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
            $g = [System.Drawing.Graphics]::FromImage($bitmap)
            $g.CopyFromScreen(0, 0, 0, 0, $bitmap.Size)
            $g.Dispose()
            [void][IO.Directory]::CreateDirectory($shots)
            $bitmap.Save((Join-Path $shots ('0-menu-explorer' + [IO.Path]::GetExtension($file).Replace('.', '-') + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            Start-Sleep -Milliseconds 500
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            $calls = Calls 5
            Write-Host ("DIAG Esplora risorse {0}: menu aperto e chiuso con Esc | in primo piano {1} | partenze: {2} {3}" -f [IO.Path]::GetExtension($file), [Win]::GetForegroundWindow(), $calls.Count, ($calls -join '; '))
            $window.Quit()
        } else {
            Write-Host 'DIAG Esplora risorse: finestra non trovata'
        }
    } catch {
        Write-Host "DIAG Esplora risorse: errore $($_.Exception.Message)"
    }
}

foreach ($keyPath in $keys) { $registry.DeleteSubKeyTree($keyPath, $false) }
