# Diagnostica temporanea del menu dinamico (non fa parte del test)
$ErrorActionPreference = 'Continue'
function W([string]$t) { [Console]::WriteLine($t) }
$src = Split-Path -Parent $PSScriptRoot
$app = Join-Path $env:LOCALAPPDATA 'SendToUnity'
$clsid = '{6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23}'

& (Join-Path $src 'install.cmd') /quiet
foreach ($f in [IO.Directory]::GetFiles($app)) { W "file: $f" }
function Try-Create([string]$what) {
    try {
        $c = [Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]$clsid))
        W "$what -> creato $($c.GetType().FullName)"
    } catch { W "$what -> errore $($_.Exception.InnerException.Message)" }
}
Try-Create 'HKCU, powershell'
$dll = [IO.Directory]::GetFiles($app, 'SendToUnityMenu-*.dll')[0]
try {
    $asm = [Reflection.Assembly]::LoadFrom($dll)
    W "assembly: $($asm.FullName)"
    $t = $asm.GetType('SendToUnity.MenuCommand')
    $o = [Activator]::CreateInstance($t)
    W "istanza diretta: $o"
} catch { W "LoadFrom: $($_.Exception.ToString())" }

New-Item -Force -Path 'HKLM:\SOFTWARE\Microsoft\Fusion' | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Fusion' -Name LogFailures -Value 1 -Type DWord
[void][IO.Directory]::CreateDirectory('C:\fusion')
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Fusion' -Name LogPath -Value 'C:\fusion\' -Type String
$out = powershell -NoProfile -Command "try { [Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]'$clsid')) | Out-Null; 'nuovo processo HKCU -> creato' } catch { 'nuovo processo HKCU -> ' + `$_.Exception.InnerException.Message }"
W ($out -join ' ')
foreach ($f in [IO.Directory]::GetFiles('C:\fusion', '*.htm', 'AllDirectories')) {
    if ($f -notmatch 'SendToUnity') { continue }
    W "=== $f"
    foreach ($l in [IO.File]::ReadAllLines($f)) { W "  $l" }
}
reg copy "HKCU\Software\Classes\CLSID\$clsid" "HKLM\Software\Classes\CLSID\$clsid" /s /f
$out = powershell -NoProfile -Command "try { [Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]'$clsid')) | Out-Null; 'nuovo processo HKLM -> creato' } catch { 'nuovo processo HKLM -> ' + `$_.Exception.InnerException.Message }"
W ($out -join ' ')
exit 0
