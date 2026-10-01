# Test end-to-end di "Invia a Unity" su una vera macchina Windows (GitHub Actions).
# Installa il programma, simula Unity Hub e un editor aperto, apre il vero menu del tasto destro
# di Esplora risorse, usa le finestre e controlla i file arrivati. Esce con 1 se un controllo fallisce.

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

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class Win32Window {
    public IntPtr Handle;
    public string Class;
    public string Text;
    public int Id;
    public int Left;
    public int Top;
    public int Width;
    public int Height;
}

// Finestre e controlli di un altro processo, con le API Win32
public static class Win {
    delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);

    static Win32Window Describe(IntPtr h) {
        var name = new StringBuilder(256);
        GetClassName(h, name, 256);
        var text = new StringBuilder(2048);
        IntPtr ignored;
        SendMessageTimeout(h, 0x000D, (IntPtr)2048, text, 0x0002, 2000, out ignored);   // WM_GETTEXT, anche tra processi
        Rect r;
        GetWindowRect(h, out r);
        return new Win32Window {
            Handle = h, Class = name.ToString(), Text = text.ToString(), Id = GetDlgCtrlID(h),
            Left = r.Left, Top = r.Top, Width = r.Right - r.Left, Height = r.Bottom - r.Top
        };
    }

    public static List<Win32Window> TopWindows(int processId) {
        var list = new List<Win32Window>();
        EnumWindows(delegate(IntPtr h, IntPtr data) {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)processId && IsWindowVisible(h)) list.Add(Describe(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static List<Win32Window> Children(IntPtr parent) {
        var list = new List<Win32Window>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr data) { list.Add(Describe(h)); return true; }, IntPtr.Zero);
        return list;
    }
}

// Il vero menu del tasto destro di Esplora risorse (lo stesso IContextMenu che usa Explorer)
public static class ExplorerMenu {
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder {
        [PreserveSig] int ParseDisplayName(IntPtr window, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
        [PreserveSig] int EnumObjects(IntPtr window, int flags, out IntPtr list);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr result);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr window, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr result);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, StringBuilder name, int max);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2 {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, StringBuilder name, int max);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr w, IntPtr l);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InvokeInfo {
        public int Size;
        public int Mask;
        public IntPtr Window;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public int HotKey;
        public IntPtr Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MenuItemInfo {
        public int Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public IntPtr SubMenu;
        public IntPtr Checked;
        public IntPtr Unchecked;
        public IntPtr Data;
        public IntPtr TypeData;
        public uint Length;
        public IntPtr Bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll")] static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr folder, out IntPtr child);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MenuItemInfo info);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr memory);

    const uint First = 1;
    const uint Last = 0x7FFF;

    public static bool Invoked;
    public static List<string> TopItems = new List<string>();

    static IContextMenu Open(string[] paths, List<IntPtr> pidls) {
        IntPtr folderPointer = IntPtr.Zero;
        var children = new IntPtr[paths.Length];
        var folderId = new Guid("000214E6-0000-0000-C000-000000000046");
        for (int i = 0; i < paths.Length; i++) {
            IntPtr pidl;
            uint attributes;
            int hr = SHParseDisplayName(paths[i], IntPtr.Zero, out pidl, 0, out attributes);
            if (hr != 0) throw new Exception("SHParseDisplayName " + hr.ToString("x") + " " + paths[i]);
            pidls.Add(pidl);
            IntPtr parent;
            IntPtr child;
            hr = SHBindToParent(pidl, ref folderId, out parent, out child);
            if (hr != 0) throw new Exception("SHBindToParent " + hr.ToString("x"));
            children[i] = child;
            if (folderPointer == IntPtr.Zero) folderPointer = parent; else Marshal.Release(parent);
        }
        var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
        Marshal.Release(folderPointer);
        var menuId = new Guid("000214E4-0000-0000-C000-000000000046");
        IntPtr menuPointer;
        int result = folder.GetUIObjectOf(IntPtr.Zero, (uint)children.Length, children, ref menuId, IntPtr.Zero, out menuPointer);
        Marshal.ReleaseComObject(folder);
        if (result != 0) throw new Exception("GetUIObjectOf " + result.ToString("x"));
        var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPointer);
        Marshal.Release(menuPointer);
        return menu;
    }

    static string Text(IntPtr menu, int position) {
        var info = new MenuItemInfo();
        info.Size = Marshal.SizeOf(typeof(MenuItemInfo));
        info.Mask = 0x40;   // MIIM_STRING
        if (!GetMenuItemInfo(menu, (uint)position, true, ref info)) return "";
        if (info.Length == 0) return "";
        info.Length++;
        info.TypeData = Marshal.AllocHGlobal((int)info.Length * 2);
        try {
            if (!GetMenuItemInfo(menu, (uint)position, true, ref info)) return "";
            return Marshal.PtrToStringUni(info.TypeData);
        } finally {
            Marshal.FreeHGlobal(info.TypeData);
        }
    }

    static uint Id(IntPtr menu, int position, out IntPtr subMenu) {
        var info = new MenuItemInfo();
        info.Size = Marshal.SizeOf(typeof(MenuItemInfo));
        info.Mask = 0x02 | 0x04;   // MIIM_ID | MIIM_SUBMENU
        GetMenuItemInfo(menu, (uint)position, true, ref info);
        subMenu = info.SubMenu;
        return info.Id;
    }

    // Voci del sottomenu "Invia a Unity" per questi elementi (null se la voce manca);
    // se click non e' null, clicca la prima voce che inizia con quel testo.
    public static List<string> Submenu(string[] paths, string click) {
        Invoked = false;
        TopItems = new List<string>();
        var pidls = new List<IntPtr>();
        IContextMenu menu = null;
        IntPtr popup = CreatePopupMenu();
        try {
            menu = Open(paths, pidls);
            int hr = menu.QueryContextMenu(popup, 0, First, Last, 0);
            if (hr < 0) throw new Exception("QueryContextMenu " + hr.ToString("x"));
            int count = GetMenuItemCount(popup);
            for (int i = 0; i < count; i++) {
                string text = Text(popup, i);
                TopItems.Add(text);
                if (text.Replace("&", "") != "Invia a Unity") continue;
                IntPtr sub;
                Id(popup, i, out sub);
                if (sub == IntPtr.Zero) return new List<string>();
                if (GetMenuItemCount(sub) == 0) {
                    var menu2 = menu as IContextMenu2;
                    if (menu2 != null) menu2.HandleMenuMsg(0x0117, sub, (IntPtr)i);   // WM_INITMENUPOPUP
                }
                var items = new List<string>();
                int n = GetMenuItemCount(sub);
                for (int j = 0; j < n; j++) {
                    string itemText = Text(sub, j);
                    items.Add(itemText);
                    if (click != null && !Invoked && itemText.StartsWith(click)) {
                        IntPtr ignored;
                        uint id = Id(sub, j, out ignored);
                        var info = new InvokeInfo();
                        info.Size = Marshal.SizeOf(typeof(InvokeInfo));
                        info.Verb = (IntPtr)(id - First);
                        info.Show = 1;
                        int result = menu.InvokeCommand(ref info);
                        if (result < 0) throw new Exception("InvokeCommand " + result.ToString("x"));
                        Invoked = true;
                    }
                }
                return items;
            }
            return null;
        } finally {
            DestroyMenu(popup);
            if (menu != null) Marshal.ReleaseComObject(menu);
            foreach (IntPtr p in pidls) CoTaskMemFree(p);
        }
    }
}
'@

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
# Sottomenu "Invia a Unity" per questi elementi; con $click clicca la voce che inizia cosi'
function Menu([string[]]$paths, [string]$click = $null) {
    try {
        $items = [ExplorerMenu]::Submenu($paths, $click)
        if ($null -eq $items) { Note ('voci principali del menu: ' + ([ExplorerMenu]::TopItems -join ' | ')) }
        , $items
    } catch {
        Note "menu non leggibile: $($_.Exception.Message)"
        , $null
    }
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

    Write-Host '--- 5. file gia presenti'
    [IO.File]::WriteAllText($albedo, 'A2')
    $proc = Start-Tool @('--to', (Q $p1), (Q $albedo))
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    Check ($null -ne $ask) 'chiede se sostituire un file gia presente'
    if ($ask) {
        Save-Shot $ask '4-sostituire'
        Press (Find-MessageButton $ask 6)   # 6 = Si'
    }
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A2') 'con Si il file viene sostituito'

    [IO.File]::WriteAllText($albedo, 'A3')
    $proc = Start-Tool @('--to', (Q $p1), (Q $albedo))
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    if ($ask) { Press (Find-MessageButton $ask 7) }   # 7 = No
    [void](Wait-Exit $proc)
    Check ((Read-File (Join-Path $dest 'roccia_albedo.png')) -eq 'A2') "con No il file resta com'era"

    Write-Host '--- 6. cartelle, dal menu'
    $flip = Join-Path $in 'Flipbook'
    New-File (Join-Path $flip 'frame_01.png') 'F1' | Out-Null
    New-File (Join-Path $flip 'sub\frame_02.png') 'F2' | Out-Null
    New-File (Join-Path $flip 'Thumbs.db') 'x' | Out-Null
    [void][IO.Directory]::CreateDirectory((Join-Path $flip 'vuota'))
    $items = Menu @($flip) 'Gioco'
    Check ([ExplorerMenu]::Invoked) 'la voce compare anche sulle cartelle'
    [void](Wait-Idle)
    $flipDest = Join-Path $dest 'Flipbook'
    Check ((Exists (Join-Path $flipDest 'frame_01.png')) -and (Exists (Join-Path $flipDest 'sub\frame_02.png'))) 'cartella copiata con le sottocartelle'
    Check (-not (Exists (Join-Path $flipDest 'Thumbs.db'))) 'Thumbs.db non viene copiato'
    Check (Exists (Join-Path $flipDest 'vuota')) 'copiate anche le cartelle vuote'
    New-File (Join-Path $flip 'frame_03.png') 'F3' | Out-Null
    $proc = Start-Tool @('--to', (Q $p1), (Q $flip))
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    if ($ask) { Press (Find-MessageButton $ask 7) }
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
    $ask = Find-Window $proc.Id 'Invia a Unity' '#32770'
    if ($ask) { Press (Find-MessageButton $ask 6) }
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

    Write-Host '--- 9. disinstallazione'
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
