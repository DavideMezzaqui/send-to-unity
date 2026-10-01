# Diagnostica (solo GitHub Actions): come costruisce Windows un sottomenu del tasto destro
# a seconda di come e' scritto nel registro, con 1 e con 20 file selezionati.
# Per ogni variante dice se il sottomenu ha voci e quante volte parte il comando
# dopo la sola lettura del menu e dopo il clic sulla voce.

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { Write-Host 'Solo su GitHub Actions.'; exit 2 }

Add-Type -Path (Join-Path $PSScriptRoot 'WinTest.cs')
$registry = [Microsoft.Win32.Registry]::CurrentUser
$work     = Join-Path $env:RUNNER_TEMP 'diag'
$marker   = Join-Path $work 'invocazioni.txt'
$verbKey  = 'Software\Classes\*\shell\STUDiag'
$extKey   = 'Software\Classes\STUDiagSub'

function Q([string]$text) { '"' + $text + '"' }

$one = Join-Path $work 'uno\a.txt'
[void][IO.Directory]::CreateDirectory((Split-Path $one))
[IO.File]::WriteAllText($one, 'x')
$twenty = @(1..20 | ForEach-Object {
    $f = Join-Path $work "venti\f$_.txt"
    [void][IO.Directory]::CreateDirectory((Split-Path $f))
    [IO.File]::WriteAllText($f, 'x')
    $f
})
$command = (Q "$env:WINDIR\System32\cmd.exe") + ' /c echo %1>>' + (Q $marker)

function Set-Variant([bool]$parentPlayer, [bool]$childPlayer, [bool]$extended) {
    $registry.DeleteSubKeyTree($verbKey, $false)
    $registry.DeleteSubKeyTree($extKey, $false)
    $key = $registry.CreateSubKey($verbKey)
    $key.SetValue('MUIVerb', 'Diagnosi Unity')
    if ($parentPlayer) { $key.SetValue('MultiSelectModel', 'Player') }
    if ($extended) {
        $key.SetValue('ExtendedSubCommandsKey', 'STUDiagSub')
        $shell = $registry.CreateSubKey("$extKey\shell")
    } else {
        $key.SetValue('SubCommands', '')
        $shell = $key.CreateSubKey('shell')
    }
    $item = $shell.CreateSubKey('00')
    $item.SetValue('MUIVerb', 'Voce uno')
    if ($childPlayer) { $item.SetValue('MultiSelectModel', 'Player') }
    $item.CreateSubKey('command').SetValue('', $command)
    $item.Close(); $shell.Close(); $key.Close()
}

function Count-Calls([int]$seconds) {
    Start-Sleep -Seconds $seconds
    if ([IO.File]::Exists($marker)) { @([IO.File]::ReadAllLines($marker) | Where-Object { $_.Trim() }).Count } else { 0 }
}

# Configurazione scelta per il programma: SubCommands + Player sulla principale e sulle sottovoci
Set-Variant $true $true $false
foreach ($selection in @(@{ Count = 1; Files = @($one); Wait = 5 }, @{ Count = 20; Files = $twenty; Wait = 10 })) {
    foreach ($upTo in 1..4) {
        Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
        try {
            $report = [ExplorerMenu]::Steps([string[]]$selection.Files, 'Diagnosi Unity', $upTo, 'Voce uno')
            $calls = Count-Calls $selection.Wait
            Write-Host ("DIAG {0} file, fino al passo {1} | {2} | partenze: {3}" -f $selection.Count, $upTo, $report, $calls)
        } catch {
            Write-Host ("DIAG {0} file, fino al passo {1} | errore: {2}" -f $selection.Count, $upTo, $_.Exception.Message)
        }
    }
}
$registry.DeleteSubKeyTree($verbKey, $false)
$registry.DeleteSubKeyTree($extKey, $false)
