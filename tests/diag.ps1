# Diagnostica temporanea del menu dinamico (non fa parte del test)
$ErrorActionPreference = 'Continue'
function W([string]$t) { [Console]::WriteLine($t) }
$src = Split-Path -Parent $PSScriptRoot
$clsid = '{6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23}'
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Diagnostics;
public static class Tok {
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr t, int cls, out int info, int len, out int ret);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    public static string Elevated(int pid) {
        IntPtr p = OpenProcess(0x1000, false, pid); IntPtr t; int v; int r;
        if (!OpenProcessToken(p, 8, out t)) return "token? " + Marshal.GetLastWin32Error();
        GetTokenInformation(t, 20, out v, 4, out r); string e = v != 0 ? "elevato" : "non elevato";
        GetTokenInformation(t, 18, out v, 4, out r); return e + ", tipo " + v;
    }
}
'@
foreach ($p in [Diagnostics.Process]::GetProcessesByName('explorer')) { W "explorer $($p.Id): $([Tok]::Elevated($p.Id))" }
W "questo powershell: $([Tok]::Elevated($PID))"

& (Join-Path $src 'install.cmd') /quiet
$probe = Join-Path $env:RUNNER_TEMP 'probe.ps1'
$result = Join-Path $env:RUNNER_TEMP 'probe.txt'
[IO.File]::WriteAllText($probe, @"
`$r = try { [void][Activator]::CreateInstance([Type]::GetTypeFromCLSID([Guid]'$clsid')); 'creato' } catch { `$_.Exception.InnerException.Message }
Add-Type -TypeDefinition '$((Get-Content -Raw $PSCommandPath) -replace "(?s).*?Add-Type @'(.*?)'@.*", '$1' -replace "'", "''")'
[IO.File]::WriteAllText('$result', `$r + ' / ' + [Tok]::Elevated(`$PID))
"@)
runas /trustlevel:0x20000 "powershell -NoProfile -ExecutionPolicy Bypass -File $probe"
for ($i = 0; $i -lt 30 -and -not (Test-Path $result); $i++) { Start-Sleep -Milliseconds 500 }
if (Test-Path $result) { W ("utente normale, solo HKCU: " + [IO.File]::ReadAllText($result)) } else { W 'utente normale: nessun risultato' }
exit 0
