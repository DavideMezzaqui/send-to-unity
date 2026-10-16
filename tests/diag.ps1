# Diagnostica temporanea del menu dinamico (non fa parte del test)
$ErrorActionPreference = 'Continue'
$src = Split-Path -Parent $PSScriptRoot
$app = Join-Path $env:LOCALAPPDATA 'SendToUnity'
$clsid = '{6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23}'
New-Item -Force -Path 'HKLM:\SOFTWARE\Microsoft\Fusion' | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Fusion' -Name EnableLog -Value 1 -Type DWord
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Fusion' -Name ForceLog -Value 1 -Type DWord
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Fusion' -Name LogFailures -Value 1 -Type DWord
New-Item -Force -ItemType Directory C:\fusion | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Fusion' -Name LogPath -Value 'C:\fusion\' -Type String

& (Join-Path $src 'install.cmd') /quiet
Get-ChildItem $app | ForEach-Object { Write-Host "file: $($_.Name) $($_.Length)" }
reg query "HKCU\Software\Classes\CLSID\$clsid" /s
function Try-Create([string]$what) {
    try {
        $c = [Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]$clsid))
        Write-Host "$what -> creato $($c.GetType().FullName)"
    } catch { Write-Host "$what -> errore $($_.Exception.InnerException.Message)" }
}
Try-Create 'HKCU, powershell'
$dll = (Get-ChildItem $app -Filter 'SendToUnityMenu-*.dll' | Select-Object -First 1).FullName
try {
    $asm = [Reflection.Assembly]::LoadFrom($dll)
    Write-Host "assembly: $($asm.FullName)"
    $t = $asm.GetType('SendToUnity.MenuCommand')
    $o = [Activator]::CreateInstance($t)
    Write-Host "istanza diretta: $o"
} catch { Write-Host "LoadFrom: $($_.Exception.Message)" }
Get-ChildItem C:\fusion -Recurse -Filter *.htm | Where-Object { $_.FullName -match 'SendToUnity' } | Select-Object -First 3 | ForEach-Object {
    Write-Host "=== $($_.FullName)"; Get-Content $_.FullName | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" } }
# copia in HKLM
reg copy "HKCU\Software\Classes\CLSID\$clsid" "HKLM\Software\Classes\CLSID\$clsid" /s /f
Try-Create 'HKLM, powershell (stesso processo)'
$job = Start-Job { try { $c = [Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]$using:clsid)); "nuovo processo -> creato $($c.GetType().FullName)" } catch { "nuovo processo -> errore $($_.Exception.InnerException.Message)" } }
Receive-Job $job -Wait | Write-Host
exit 0
