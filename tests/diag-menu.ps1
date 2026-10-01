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

function Count-Calls {
    Start-Sleep -Seconds 3
    if ([IO.File]::Exists($marker)) { @([IO.File]::ReadAllLines($marker) | Where-Object { $_.Trim() }).Count } else { 0 }
}

$variants = @(
    @{ Name = 'SubCommands, nessun Player';            Parent = $false; Child = $false; Ext = $false },
    @{ Name = 'SubCommands, Player sulla principale';  Parent = $true;  Child = $false; Ext = $false },
    @{ Name = 'SubCommands, Player sulla sottovoce';   Parent = $false; Child = $true;  Ext = $false },
    @{ Name = 'SubCommands, Player su entrambe';       Parent = $true;  Child = $true;  Ext = $false },
    @{ Name = 'ExtendedSubCommandsKey, nessun Player'; Parent = $false; Child = $false; Ext = $true },
    @{ Name = 'ExtendedSubCommandsKey, Player sottovoce'; Parent = $false; Child = $true; Ext = $true }
)

foreach ($v in $variants) {
    Set-Variant $v.Parent $v.Child $v.Ext
    foreach ($selection in @(@{ Count = 1; Files = @($one) }, @{ Count = 20; Files = $twenty })) {
        Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
        try {
            $items = [ExplorerMenu]::Submenu([string[]]$selection.Files, 'Diagnosi Unity', $null)
            $info = [ExplorerMenu]::Info
            $afterRead = Count-Calls
            $shown = if ($null -eq $items) { 'voce assente' } else { 'voci: ' + ($items -join ' / ') }
            $items = [ExplorerMenu]::Submenu([string[]]$selection.Files, 'Diagnosi Unity', 'Voce uno')
            $afterClick = Count-Calls
            Write-Host ("DIAG {0} | {1} file | {2} | {3} | partenze dopo la lettura: {4} | dopo il clic: {5}" -f $v.Name, $selection.Count, $shown, $info, $afterRead, $afterClick)
        } catch {
            Write-Host ("DIAG {0} | {1} file | errore: {2}" -f $v.Name, $selection.Count, $_.Exception.Message)
        }
    }
}
$registry.DeleteSubKeyTree($verbKey, $false)
$registry.DeleteSubKeyTree($extKey, $false)
