// Aiuti per i test su Windows: finestre di altri processi e vero menu del tasto destro.
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

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3 {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, StringBuilder name, int max);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr w, IntPtr l);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr w, IntPtr l, out IntPtr result);
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
    public static string Info = "";

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

    // Voci del sottomenu che ha come titolo parent (null se la voce manca);
    // se click non e' null, clicca la prima voce che inizia con quel testo.
    public static List<string> Submenu(string[] paths, string parent, string click) {
        Invoked = false;
        Info = "";
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
                if (text.Replace("&", "") != parent) continue;
                IntPtr sub;
                uint parentId = Id(popup, i, out sub);
                Info = "id voce " + parentId + ", sottomenu " + sub + ", voci " + (sub == IntPtr.Zero ? -1 : GetMenuItemCount(sub));
                if (sub == IntPtr.Zero) return new List<string>();
                if (GetMenuItemCount(sub) == 0) {
                    var menu3 = menu as IContextMenu3;
                    IntPtr ignoredResult;
                    if (menu3 != null) menu3.HandleMenuMsg2(0x0117, sub, (IntPtr)i, out ignoredResult);   // WM_INITMENUPOPUP
                    Info += ", dopo HandleMenuMsg2 " + GetMenuItemCount(sub);
                }
                if (GetMenuItemCount(sub) == 0) {
                    var menu2 = menu as IContextMenu2;
                    if (menu2 != null) menu2.HandleMenuMsg(0x0117, sub, (IntPtr)i);
                    Info += ", dopo HandleMenuMsg " + GetMenuItemCount(sub);
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
