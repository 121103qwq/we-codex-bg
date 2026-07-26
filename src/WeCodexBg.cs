// WeCodexBg.cs -- same helper as src/we_codex_bg.cpp, in a single C# file so it can
// be built with the csc.exe that ships with .NET Framework 4.x (no toolchain to
// install).  Same options, same four modes, same restore-on-exit guarantees.
//
// Build:  build.bat cs        (or see build.bat for the exact csc.exe line)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class WeCodexBg
{
    // ------------------------------------------------------------------ win32 --

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;

    const long WS_CHILD = 0x40000000, WS_POPUP = unchecked((long)0x80000000);
    const long WS_VISIBLE = 0x10000000;
    const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    const long WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    const long WS_SYSMENU = 0x00080000, WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000;
    const long WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;

    const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_WINDOWEDGE = 0x100;
    const long WS_EX_CLIENTEDGE = 0x200, WS_EX_DLGMODALFRAME = 0x1, WS_EX_STATICEDGE = 0x20000;
    const long WS_EX_APPWINDOW = 0x40000, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;

    // ex-styles this tool adds to the wallpaper window (cleared again by --restore)
    const long WALL_EX_ADDED = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED;

    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const uint SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200, SWP_NOSENDCHANGING = 0x400;

    const uint LWA_ALPHA = 0x2;
    const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
    const uint GA_PARENT = 1;

    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_HIDE = 0x8003;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0x0, WINEVENT_SKIPOWNPROCESS = 0x2;
    const int OBJID_WINDOW = 0;

    const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_QUIT = 0x12, WM_TIMER = 0x113;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG
    { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
    delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);
    delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint tid, uint time);
    delegate bool ConsoleCtrlDelegate(uint type);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowLongPtrW(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod,
        WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle,
        string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, IntPtr id);
    [DllImport("user32.dll")] static extern bool SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int a, int b, int c, int d, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate cb, bool add);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(
        IntPtr proc, uint flags, StringBuilder path, ref uint size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l,
                                                                     uint flags, uint ms, out IntPtr res);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    // ------------------------------------------------------------------ options --

    enum Mode { Composite, Embed, Alpha, Overlay }
    enum Place { ChildBottom, TopLevelBelow, TopLevelAbove }

    static Place PlaceOf(Mode m)
    {
        switch (m)
        {
            case Mode.Composite:
            case Mode.Embed: return Place.ChildBottom;
            case Mode.Alpha: return Place.TopLevelBelow;
            default: return Place.TopLevelAbove;
        }
    }

    class Options
    {
        public string Title = "", Class = "", Exe = "", ContentClass = "";
        public uint Pid;
        public string WeExe = "", Wallpaper = "", AttachTitle = "";
        public string WeWindow = "CodexWallpaperHost";
        // alpha default: it only layers the TOP-LEVEL window, which every host
        // tolerates.  composite touches the content child and is unsafe on Chromium.
        public Mode Mode = Mode.Alpha;
        public byte Alpha = 235, Film = 70, WallAlpha = 255;
        public bool ClientOnly = true, KeepWe = false, Fallback = true;
        public int Fps = 30, Round = 0;
        public bool ListOnly, TreeOnly, RestoreOnly;
    }

    // -------------------------------------------------------------------- state --

    static IntPtr _target, _wall, _content, _msgWnd;
    static IntPtr _targetEx, _contentEx, _wallStyle, _wallEx, _wallParent;
    static bool _targetExSaved, _contentExSaved, _wallSaved, _weLaunched;
    static RECT _lastRect;
    static bool _wallHidden, _restored = true;
    static volatile bool _stopping;
    static Mode _mode = Mode.Composite;
    static Place _place = Place.ChildBottom;
    static bool _clientOnly = true, _keepWe, _verbose;
    // live-adjustable opacity, driven by WM_APP+1 / WM_APP+2 from the UI
    const uint WM_SET_HOST_ALPHA = 0x8000 + 1, WM_SET_WALL_ALPHA = 0x8000 + 2;
    static byte _liveHostAlpha = 255, _liveWallAlpha = 255;
    static int _round;
    static uint _mainThread;

    // keep delegates alive for the lifetime of the process
    static WndProcDelegate _wndProc;
    static WinEventDelegate _winEvent;
    static ConsoleCtrlDelegate _ctrlHandler;

    static void Log(string s) { Console.WriteLine(s); }
    static void LogV(string s) { if (_verbose) Console.WriteLine(s); }

    // -------------------------------------------------------------- small utils --

    static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
    static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    static bool Has(string hay, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return true;
        return hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
    static bool Cloaked(IntPtr h)
    {
        int v;
        return DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out v, 4) == 0 && v != 0;
    }
    static int Area(RECT r) { return (r.R - r.L) * (r.B - r.T); }
    static bool Same(RECT a, RECT b) { return a.L == b.L && a.T == b.T && a.R == b.R && a.B == b.B; }
    static long Style(IntPtr h, int idx) { return GetWindowLongPtrW(h, idx).ToInt64(); }
    static void SetStyle(IntPtr h, int idx, long v) { SetWindowLongPtrW(h, idx, new IntPtr(v)); }

    static string ExePath(uint pid)
    {
        IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(600);
            uint n = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(p, 0, sb, ref n) ? sb.ToString() : "";
        }
        finally { CloseHandle(p); }
    }
    static string ExeName(uint pid)
    {
        string full = ExePath(pid);
        if (full == "") return "";
        return System.IO.Path.GetFileName(full).ToLowerInvariant();
    }
    static bool IsWe(string exe)
    {
        switch (exe)
        {
            case "wallpaper64.exe": case "wallpaper32.exe":
            case "webwallpaper64.exe": case "webwallpaper32.exe":
            case "wallpaperwindows.exe":
            case "wallpaperservice64_engine.exe": case "wallpaperservice32_engine.exe": return true;
            default: return false;
        }
    }

    // ------------------------------------------------------------ window lookup --

    class WinInfo { public IntPtr H; public uint Pid; public string Title, Cls, Exe; public RECT Rc; }

    static List<WinInfo> TopLevel()
    {
        var list = new List<WinInfo>();
        var exeCache = new Dictionary<uint, string>();
        EnumWindows((h, lp) =>
        {
            if (!IsWindowVisible(h)) return true;
            RECT rc;
            if (!GetWindowRect(h, out rc)) return true;
            if (rc.R - rc.L < 80 || rc.B - rc.T < 60) return true;
            if (Cloaked(h)) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            string exe;
            if (!exeCache.TryGetValue(pid, out exe)) { exe = ExeName(pid); exeCache[pid] = exe; }
            list.Add(new WinInfo { H = h, Pid = pid, Title = Text(h), Cls = Cls(h), Exe = exe, Rc = rc });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static IntPtr FindTarget(Options o)
    {
        bool defaults = o.Title == "" && o.Class == "" && o.Exe == "" && o.Pid == 0;
        IntPtr fg = GetForegroundWindow(), best = IntPtr.Zero;
        int bestScore = 0;
        foreach (var w in TopLevel())
        {
            if (w.H == _msgWnd || IsWe(w.Exe)) continue;
            if (o.Pid != 0 && w.Pid != o.Pid) continue;
            if (o.Exe != "" && !string.Equals(w.Exe, o.Exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (o.Class != "" && !Has(w.Cls, o.Class)) continue;
            if (o.Title != "" && !Has(w.Title, o.Title)) continue;
            if (defaults)
            {
                bool byExe = w.Exe == "chatgpt.exe" || w.Exe == "codex.exe" ||
                             w.Exe == "openai.exe" || w.Exe == "chatgpt-desktop.exe";
                bool byTitle = Has(w.Title, "codex") || Has(w.Title, "chatgpt");
                if (!byExe && !byTitle) continue;
            }
            int score = Area(w.Rc) + (w.H == fg ? 1 : 0);
            if (score > bestScore) { bestScore = score; best = w.H; }
        }
        return best;
    }

    // Name matching deliberately includes HIDDEN windows.  PrepareWall() hides the
    // window while restyling it, so a hard-killed run leaves a hidden window still
    // holding the name - and WE then silently refuses to create another one with
    // that name, wedging every later run.
    static IntPtr FindWeWindow(Options o, out bool exact)
    {
        IntPtr named = IntPtr.Zero, best = IntPtr.Zero;
        int bestArea = 0;
        var exeCache = new Dictionary<uint, string>();
        EnumWindows((h, lp) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            string exe;
            if (!exeCache.TryGetValue(pid, out exe)) { exe = ExeName(pid); exeCache[pid] = exe; }
            if (!IsWe(exe)) return true;

            string title = Text(h);
            if (o.WeWindow != "" && string.Equals(title, o.WeWindow, StringComparison.OrdinalIgnoreCase))
            { named = h; return false; }
            if (o.AttachTitle != "" && Has(title, o.AttachTitle)) { named = h; return false; }
            if (string.Equals(title, "Wallpaper Engine", StringComparison.OrdinalIgnoreCase)) return true;

            if (!IsWindowVisible(h) || Cloaked(h)) return true;   // guess-by-size: visible only
            RECT rc;
            if (!GetWindowRect(h, out rc)) return true;
            if (rc.R - rc.L < 80 || rc.B - rc.T < 60) return true;
            int a = Area(rc);
            if (a > bestArea) { bestArea = a; best = h; }
            return true;
        }, IntPtr.Zero);

        exact = named != IntPtr.Zero;
        return named != IntPtr.Zero ? named : best;
    }

    // Close any wallpaper window still holding our -playInWindow name and wait for it
    // to go away, so WE will hand that name out again.
    static int CloseStaleWallpaperWindows(Options o)
    {
        if (o.WeWindow == "") return 0;
        int closed = 0;
        for (int pass = 0; pass < 20; pass++)
        {
            bool exact;
            IntPtr h = FindWeWindow(o, out exact);
            if (!exact || h == IntPtr.Zero) break;
            if (pass == 0)
                Log("[i] 发现残留的壁纸窗口（名称 " + o.WeWindow + "），先关掉它 —— " +
                    "否则 Wallpaper Engine 不会再用这个名字开新窗口。");
            PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            closed++;
            System.Threading.Thread.Sleep(250);
        }
        return closed;
    }

    static string FindWeExe(Options o)
    {
        if (o.WeExe != "") return o.WeExe;
        foreach (var w in TopLevel())
        {
            if (!IsWe(w.Exe)) continue;
            string full = ExePath(w.Pid);
            if (full == "") continue;
            string dir = System.IO.Path.GetDirectoryName(full) + "\\";
            if (System.IO.File.Exists(dir + "wallpaper64.exe")) return dir + "wallpaper64.exe";
            if (System.IO.File.Exists(dir + "wallpaper32.exe")) return dir + "wallpaper32.exe";
            return full;
        }
        foreach (var r in SteamRoots())
        {
            string dir = r + @"\steamapps\common\wallpaper_engine\";
            if (System.IO.File.Exists(dir + "wallpaper64.exe")) return dir + "wallpaper64.exe";
            if (System.IO.File.Exists(dir + "wallpaper32.exe")) return dir + "wallpaper32.exe";
        }
        return "";
    }

    // Steam is frequently NOT under Program Files, so ask the registry first and
    // then follow libraryfolders.vdf to the libraries on other drives.
    static List<string> SteamRoots()
    {
        var roots = new List<string>();
        Action<string> add = p =>
        {
            if (string.IsNullOrEmpty(p)) return;
            p = p.Replace('/', '\\').TrimEnd('\\');
            if (p.Length == 0) return;
            foreach (var r in roots)
                if (string.Equals(r, p, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(p);
        };
        Func<string, string, string> reg = (k, n) =>
        {
            try { return Microsoft.Win32.Registry.GetValue(k, n, null) as string; }
            catch { return null; }
        };
        add(reg(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"));
        add(reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        add(reg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"));
        add(@"C:\Program Files (x86)\Steam");
        add(@"C:\Program Files\Steam");
        add(@"D:\Steam");
        add(@"D:\SteamLibrary");
        add(@"E:\Steam");
        add(@"E:\SteamLibrary");

        for (int i = 0; i < roots.Count; i++)
        {
            foreach (string rel in new[] { @"\steamapps\libraryfolders.vdf", @"\config\libraryfolders.vdf" })
            {
                try
                {
                    string p = roots[i] + rel;
                    if (!System.IO.File.Exists(p)) continue;
                    string vdf = System.IO.File.ReadAllText(p);
                    int k = 0;
                    while ((k = vdf.IndexOf("\"path\"", k, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        k += 6;
                        int q1 = vdf.IndexOf('"', k);
                        if (q1 < 0) break;
                        int q2 = vdf.IndexOf('"', q1 + 1);
                        if (q2 < 0) break;
                        add(vdf.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\"));
                        k = q2 + 1;
                    }
                }
                catch { }
            }
        }
        return roots;
    }

    // ------------------------------------------------------------- geometry sync --

    static bool TargetRectOnScreen(IntPtr t, bool clientOnly, out RECT outRc)
    {
        outRc = new RECT();
        if (!IsWindow(t)) return false;
        if (!clientOnly) return GetWindowRect(t, out outRc);
        RECT rc;
        if (!GetClientRect(t, out rc)) return false;
        POINT a = new POINT { X = rc.L, Y = rc.T }, b = new POINT { X = rc.R, Y = rc.B };
        if (!ClientToScreen(t, ref a) || !ClientToScreen(t, ref b)) return false;
        outRc = new RECT { L = a.X, T = a.Y, R = b.X, B = b.Y };
        return true;
    }
    static bool TargetRectInParent(IntPtr t, bool clientOnly, out RECT outRc)
    {
        outRc = new RECT();
        if (clientOnly) return GetClientRect(t, out outRc);
        RECT wr;
        if (!GetWindowRect(t, out wr)) return false;
        POINT p = new POINT { X = wr.L, Y = wr.T };
        if (!ScreenToClient(t, ref p)) return false;
        outRc = new RECT { L = p.X, T = p.Y, R = p.X + (wr.R - wr.L), B = p.Y + (wr.B - wr.T) };
        return true;
    }

    static void Sync(bool force)
    {
        if (_stopping) return;
        if (_target == IntPtr.Zero || !IsWindow(_target) || _wall == IntPtr.Zero || !IsWindow(_wall))
        {
            PostThreadMessageW(_mainThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        bool asChild = _place == Place.ChildBottom;
        if (!asChild)
        {
            bool visible = IsWindowVisible(_target) && !IsIconic(_target) && !Cloaked(_target);
            if (!visible)
            {
                if (!_wallHidden) { ShowWindow(_wall, SW_HIDE); _wallHidden = true; LogV("[sync] target hidden -> wallpaper hidden"); }
                return;
            }
            if (_wallHidden) { _wallHidden = false; force = true; }
        }

        RECT r;
        if (asChild) { if (!TargetRectInParent(_target, _clientOnly, out r)) return; }
        else { if (!TargetRectOnScreen(_target, _clientOnly, out r)) return; }
        int w = r.R - r.L, h = r.B - r.T;
        if (w <= 0 || h <= 0) return;

        bool sizeChanged = (w != _lastRect.R - _lastRect.L) || (h != _lastRect.B - _lastRect.T);
        bool rectChanged = !Same(r, _lastRect);

        IntPtr after;
        bool zBad;
        switch (_place)
        {
            case Place.ChildBottom:
                after = HWND_BOTTOM;
                zBad = GetWindow(_wall, GW_HWNDNEXT) != IntPtr.Zero;
                break;
            case Place.TopLevelBelow:
                after = _target;
                zBad = GetWindow(_target, GW_HWNDNEXT) != _wall;
                break;
            default:
                zBad = GetWindow(_wall, GW_HWNDNEXT) != _target;
                IntPtr above = GetWindow(_target, GW_HWNDPREV);
                if (above == _wall) above = GetWindow(_wall, GW_HWNDPREV);
                after = above != IntPtr.Zero ? above : HWND_TOP;
                break;
        }

        if (force || rectChanged || zBad)
        {
            uint flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
            if (!zBad && !force) flags |= SWP_NOZORDER;
            SetWindowPos(_wall, after, r.L, r.T, w, h, flags);
            _lastRect = r;
            if (_round > 0 && (sizeChanged || force))
            {
                IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, _round * 2, _round * 2);
                if (rgn != IntPtr.Zero && !SetWindowRgn(_wall, rgn, true)) DeleteObject(rgn);
            }
        }
        if (!asChild && force) ShowWindow(_wall, SW_SHOWNOACTIVATE);
    }

    // ------------------------------------------------------------- style surgery --

    static void ForceRepaint(IntPtr h)
    {
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    static void PrepareWall()
    {
        _wallStyle = GetWindowLongPtrW(_wall, GWL_STYLE);
        _wallEx = GetWindowLongPtrW(_wall, GWL_EXSTYLE);
        _wallParent = GetAncestor(_wall, GA_PARENT);          // real parent, not the owner
        if (_wallParent == GetDesktopWindow()) _wallParent = IntPtr.Zero;
        _wallSaved = true;
        _restored = false;

        bool asChild = _place == Place.ChildBottom;
        ShowWindow(_wall, SW_HIDE);

        long st = _wallStyle.ToInt64() & ~WS_VISIBLE;        // it is hidden right now
        st &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
        st |= WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        if (asChild) { st &= ~WS_POPUP; st |= WS_CHILD; }
        else { st &= ~WS_CHILD; st |= WS_POPUP; }
        SetStyle(_wall, GWL_STYLE, st);

        long ex = _wallEx.ToInt64();
        ex &= ~(WS_EX_APPWINDOW | WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_WINDOWEDGE | WS_EX_STATICEDGE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetStyle(_wall, GWL_EXSTYLE, ex);

        if (asChild) SetParent(_wall, _target);
        else SetWindowPos(_wall, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        MakeClickThroughTree(_wall);          // the wallpaper must never eat a click

        // Layer the wallpaper in every mode (not just overlay) so its brightness can be
        // dialled down live.  Dimming the wallpaper preserves host contrast far better
        // than fading the host does, which matters with a bright wallpaper.
        SetStyle(_wall, GWL_EXSTYLE, Style(_wall, GWL_EXSTYLE) | WS_EX_LAYERED);
        SetLayeredWindowAttributes(_wall, 0, _liveWallAlpha, LWA_ALPHA);

        SetWindowPos(_wall, IntPtr.Zero, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        ShowWindow(_wall, SW_SHOWNOACTIVATE);
        _wallHidden = false;
        LogV(string.Format("[wall] style {0:X}->{1:X} ex {2:X}->{3:X}", _wallStyle.ToInt64(), st, _wallEx.ToInt64(), ex));
    }

    static IntPtr PickContentChild(IntPtr target, string clsFilter)
    {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        EnumChildWindows(target, (c, lp) =>
        {
            if (c == _wall) return true;                     // never pick our own wallpaper
            if (clsFilter != "") { if (!Has(Cls(c), clsFilter)) return true; }
            else if (GetParent(c) != target) return true;
            if (!IsWindowVisible(c)) return true;
            RECT rc;
            if (!GetWindowRect(c, out rc)) return true;
            int a = Area(rc);
            if (a > bestArea) { bestArea = a; best = c; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // WS_EX_TRANSPARENT is not inherited by children, so stamp the whole wallpaper
    // tree or a child renderer HWND would still swallow clicks.
    static void MakeClickThroughTree(IntPtr root)
    {
        SetStyle(root, GWL_EXSTYLE, Style(root, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        EnumChildWindows(root, (c, lp) =>
        {
            SetStyle(c, GWL_EXSTYLE, Style(c, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            return true;
        }, IntPtr.Zero);
    }

    static int _hangStrikes;
    static bool TargetResponsive(IntPtr t, uint ms)
    {
        IntPtr res;
        return SendMessageTimeout(t, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero, 0x2 /*ABORTIFHUNG*/, ms, out res) != IntPtr.Zero;
    }

    static bool MakeLayered(IntPtr h, byte alpha)
    {
        long ex = Style(h, GWL_EXSTYLE);
        SetStyle(h, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        if (!SetLayeredWindowAttributes(h, 0, alpha, LWA_ALPHA))
        {
            Log(string.Format("[!] SetLayeredWindowAttributes(0x{0:X}) 失败，err={1}",
                              h.ToInt64(), Marshal.GetLastWin32Error()));
            return false;
        }
        ForceRepaint(h);
        return true;
    }

    static bool ApplyCompositing(Options o, Mode m)
    {
        switch (m)
        {
            case Mode.Embed:
                return true;
            case Mode.Composite:
                {
                    IntPtr c = PickContentChild(_target, o.ContentClass);
                    if (c == IntPtr.Zero)
                    {
                        Log("[!] 合成模式：未找到内容子窗口 —— 请用 --tree 查看后");
                        Log("    传入 --content-class <类名片段>，或改用 --mode alpha。");
                        return false;
                    }
                    // Never add WS_EX_LAYERED to a child that already carries
                    // WS_EX_TRANSPARENT: the pair makes it truly click-through, so the
                    // host paints normally but gets no mouse input.  Chromium/Electron
                    // hosts ship Chrome_RenderWidgetHostHWND that way.
                    if ((Style(c, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0)
                    {
                        Log("[!] 合成模式不可用：内容窗口 " + Cls(c) + " 本身已带 WS_EX_TRANSPARENT，");
                        Log("    再叠加 WS_EX_LAYERED 会让宿主界面完全收不到鼠标输入（Electron/Chromium 宿主的已知问题）。");
                        Log("    自动改用更安全的模式。");
                        return false;
                    }
                    _content = c;
                    _contentEx = GetWindowLongPtrW(c, GWL_EXSTYLE);
                    _contentExSaved = true;
                    _restored = false;
                    Log(string.Format("[i] 内容窗口 0x{0:X}  类名={1}  不透明度={2}", c.ToInt64(), Cls(c), o.Alpha));
                    return MakeLayered(c, o.Alpha);
                }
            case Mode.Alpha:
                _targetEx = GetWindowLongPtrW(_target, GWL_EXSTYLE);
                _targetExSaved = true;
                _restored = false;
                return MakeLayered(_target, o.Alpha);
            default:
                return MakeLayered(_wall, o.Film);
        }
    }

    // ------------------------------------------------------------------ restore --

    static void RestoreAll()
    {
        if (_restored) return;
        _stopping = true;
        _restored = true;

        if (_contentExSaved && _content != IntPtr.Zero && IsWindow(_content))
        {
            SetWindowLongPtrW(_content, GWL_EXSTYLE, _contentEx);
            ForceRepaint(_content);
        }
        if (_targetExSaved && _target != IntPtr.Zero && IsWindow(_target))
        {
            SetWindowLongPtrW(_target, GWL_EXSTYLE, _targetEx);
            ForceRepaint(_target);
        }
        if (_wallSaved && _wall != IntPtr.Zero && IsWindow(_wall))
        {
            SetWindowRgn(_wall, IntPtr.Zero, true);
            // styles first, then un-parent (a WS_CHILD window of the desktop is broken)
            SetWindowLongPtrW(_wall, GWL_STYLE, _wallStyle);
            SetWindowLongPtrW(_wall, GWL_EXSTYLE, _wallEx);
            if (_place == Place.ChildBottom) SetParent(_wall, _wallParent);
            SetWindowPos(_wall, IntPtr.Zero, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (_weLaunched && !_keepWe) PostMessageW(_wall, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            else ShowWindow(_wall, SW_SHOWNOACTIVATE);
        }
        if (_target != IntPtr.Zero && IsWindow(_target)) ForceRepaint(_target);
        Log("[i] 已恢复窗口原始状态。");
    }

    static void RestoreOnly(Options o)
    {
        int stale = CloseStaleWallpaperWindows(o);   // safe even with no target
        if (stale > 0) Log("[i] 已清理 " + stale + " 个残留的壁纸窗口。");

        IntPtr t = FindTarget(o);
        if (t == IntPtr.Zero) { Log("[!] 未找到目标窗口（残留壁纸窗口已清理）。"); return; }

        var all = new List<IntPtr>();
        all.Add(t);
        EnumChildWindows(t, (c, lp) => { all.Add(c); return true; }, IntPtr.Zero);   // snapshot first

        int fixedCount = 0;
        foreach (IntPtr h in all)
        {
            if (!IsWindow(h)) continue;
            long ex = Style(h, GWL_EXSTYLE);
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            bool isWe = h != t && IsWe(ExeName(pid));

            if (isWe)
            {
                SetWindowRgn(h, IntPtr.Zero, true);
                SetStyle(h, GWL_EXSTYLE, ex & ~WALL_EX_ADDED);
                long st = Style(h, GWL_STYLE);
                SetStyle(h, GWL_STYLE, (st & ~WS_CHILD) | WS_POPUP);
                SetParent(h, IntPtr.Zero);
                SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                ShowWindow(h, SW_SHOWNOACTIVATE);
                fixedCount++;
            }
            else if ((ex & WS_EX_LAYERED) != 0)
            {
                SetStyle(h, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                ForceRepaint(h);
                fixedCount++;
            }
        }
        Log(string.Format("[i] 已修复 0x{1:X} 下的 {0} 个窗口。", fixedCount, t.ToInt64()));
    }

    // -------------------------------------------------------------- diagnostics --

    static void PrintList()
    {
        Log(string.Format("{0,-18} {1,-7} {2,-24} {3,-30} {4}", "HWND", "PID", "EXE", "CLASS", "TITLE / RECT"));
        foreach (var w in TopLevel())
            Log(string.Format("0x{0,-16:X} {1,-7} {2,-24} {3,-30} {4}  [{5},{6} {7}x{8}]",
                w.H.ToInt64(), w.Pid, w.Exe, w.Cls, w.Title, w.Rc.L, w.Rc.T, w.Rc.R - w.Rc.L, w.Rc.B - w.Rc.T));
    }

    static void PrintTree(IntPtr root, int depth)
    {
        RECT rc;
        GetWindowRect(root, out rc);
        Log(string.Format("{0}0x{1:X}  {2,-34} {3,-22} [{4}x{5}]{6}",
            new string(' ', depth * 2), root.ToInt64(), Cls(root), Text(root),
            rc.R - rc.L, rc.B - rc.T, IsWindowVisible(root) ? "" : "  (hidden)"));
        EnumChildWindows(root, (c, lp) =>
        {
            if (GetParent(c) == root) PrintTree(c, depth + 1);
            return true;
        }, IntPtr.Zero);
    }

    static void Usage()
    {
        Log(@"we-codex-bg  --  Wallpaper Engine live wallpaper behind the Codex/ChatGPT window

Usage: we-codex-bg.exe [options]

Modes
  --mode composite   embed the wallpaper as the bottom-most child window and fade
                     only the content child window (frame stays opaque)  [default]
  --mode embed       embed only, no transparency (plumbing test)
  --mode alpha       wallpaper pinned below the window + whole window translucent
  --mode overlay     wallpaper pinned above the window as a click-through film;
                     the Codex window itself is never modified
  --alpha 0-255      host opacity for composite / alpha (default 235)
  --film 0-255       wallpaper opacity for overlay (default 70)
  --wall-alpha 0-255 wallpaper brightness in composite/alpha/embed (default 255);
                     lower it to stop a bright wallpaper washing the host out
  --content-class <s>  which child window class to fade in composite mode
  --no-fallback      do not auto-fall back to the next mode when one fails

Target window
  --title <substr>   match window title (default: Codex / ChatGPT)
  --class <substr>   match window class
  --exe <name.exe>   match process image name
  --pid <n>          match process id

Wallpaper Engine
  --we <path>        wallpaper64.exe (auto-detected if omitted)
  --wallpaper <path> project.json / mp4 / gif ...; launches WE for you
  --we-window <name> -playInWindow name (default CodexWallpaperHost)
  --attach-title <s> attach to an already open WE window by title substring
  --keep-we          leave the wallpaper window open on exit

Geometry / misc
  --full             cover the whole window instead of the client area
  --round <px>       rounded corners for the wallpaper window (default 0)
  --fps <n>          fallback poll rate (default 30)
  --list             list top-level windows and exit
  --tree             dump the target's child window tree and exit
  --restore          undo leftovers from a hard-killed run and exit
  -v                 verbose");
    }

    // ---------------------------------------------------------------------- main --

    static uint ParseUInt(string s, uint fallback)
    {
        uint v;
        return uint.TryParse(s, out v) ? v : fallback;
    }
    static int ParseInt(string s, int fallback)
    {
        int v;
        return int.TryParse(s, out v) ? v : fallback;
    }

    static bool ParseArgs(string[] a, Options o)
    {
        for (int i = 0; i < a.Length; i++)
        {
            string cur = a[i];
            bool has = i + 1 < a.Length;
            if (!has)
            {
                switch (cur)
                {
                    case "--title": case "--class": case "--exe": case "--pid":
                    case "--we": case "--wallpaper": case "--we-window": case "--attach-title":
                    case "--content-class": case "--mode": case "--alpha": case "--film":
                    case "--fps": case "--round":
                        Log("[!] " + cur + " needs a value");
                        return false;
                }
            }
            switch (cur)
            {
                case "--title": o.Title = a[++i]; break;
                case "--class": o.Class = a[++i]; break;
                case "--exe": o.Exe = a[++i]; break;
                case "--pid": o.Pid = ParseUInt(a[++i], 0); break;
                case "--we": o.WeExe = a[++i]; break;
                case "--wallpaper": o.Wallpaper = a[++i]; break;
                case "--we-window": o.WeWindow = a[++i]; break;
                case "--attach-title": o.AttachTitle = a[++i]; break;
                case "--content-class": o.ContentClass = a[++i]; break;
                case "--keep-we": o.KeepWe = true; break;
                case "--no-fallback": o.Fallback = false; break;
                case "--mode":
                    switch (a[++i].ToLowerInvariant())
                    {
                        case "composite": o.Mode = Mode.Composite; break;
                        case "embed": o.Mode = Mode.Embed; break;
                        case "alpha": o.Mode = Mode.Alpha; break;
                        case "overlay": o.Mode = Mode.Overlay; break;
                        default: Log("[!] unknown mode: " + a[i]); return false;
                    }
                    break;
                case "--alpha": o.Alpha = (byte)(ParseUInt(a[++i], 205) & 0xFF); break;
                case "--film": o.Film = (byte)(ParseUInt(a[++i], 70) & 0xFF); break;
                case "--wall-alpha": o.WallAlpha = (byte)(ParseUInt(a[++i], 255) & 0xFF); break;
                case "--fps": o.Fps = ParseInt(a[++i], 30); break;
                case "--round": o.Round = ParseInt(a[++i], 0); break;
                case "--full": o.ClientOnly = false; break;
                case "--list": o.ListOnly = true; break;
                case "--tree": o.TreeOnly = true; break;
                case "--restore": o.RestoreOnly = true; break;
                case "-v": case "--verbose": _verbose = true; break;
                case "-h": case "--help": Usage(); return false;
                default: Log("[!] unknown argument: " + cur); Usage(); return false;
            }
        }
        if (o.Fps < 1) o.Fps = 1;
        if (o.Fps > 120) o.Fps = 120;
        return true;
    }

    static bool LaunchWallpaper(Options o, RECT rc)
    {
        string exe = FindWeExe(o);
        if (exe == "")
        {
            Log("[!] 未找到 wallpaper64.exe —— 请用 --we \"...\\wallpaper_engine\\wallpaper64.exe\" 指定");
            return false;
        }
        string args = string.Format(
            "-control openWallpaper -file \"{0}\" -playInWindow \"{1}\" -width {2} -height {3} -x {4} -y {5} -borderless",
            o.Wallpaper, o.WeWindow, rc.R - rc.L, rc.B - rc.T, rc.L, rc.T);
        Log("[we] \"" + exe + "\" " + args);
        try
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception e) { Log("[!] 启动壁纸失败：" + e.Message); return false; }
        _weLaunched = true;
        return true;
    }

    static void UndoAttempt()
    {
        bool keep = _keepWe;
        _keepWe = true;
        RestoreAll();
        _keepWe = keep;
        _stopping = false;
        _restored = true;
        _content = IntPtr.Zero;
        _contentExSaved = _targetExSaved = _wallSaved = false;
        _lastRect = new RECT();
    }
    static void CloseLaunchedWallpaper()
    {
        if (_weLaunched && !_keepWe && _wall != IntPtr.Zero && IsWindow(_wall))
            PostMessageW(_wall, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    static int Main(string[] rawArgs)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        var o = new Options();
        if (!ParseArgs(rawArgs, o)) return 1;

        _mainThread = GetCurrentThreadId();
        _keepWe = o.KeepWe;
        _clientOnly = o.ClientOnly;
        _round = o.Round;
        _liveHostAlpha = o.Alpha;
        _liveWallAlpha = o.Mode == Mode.Overlay ? o.Film : o.WallAlpha;

        if (o.ListOnly) { PrintList(); return 0; }
        if (o.RestoreOnly) { RestoreOnly(o); return 0; }

        _target = FindTarget(o);
        if (_target == IntPtr.Zero)
        {
            Log("[!] 未找到 Codex/ChatGPT 窗口。请先运行 --list，再用 --pid / --title 指定。");
            return 2;
        }
        Log(string.Format("[i] 目标窗口 0x{0:X}  类名={1}  标题={2}", _target.ToInt64(), Cls(_target), Text(_target)));

        if (o.TreeOnly) { PrintTree(_target, 0); return 0; }

        RECT rc;
        if (!TargetRectOnScreen(_target, o.ClientOnly, out rc)) { Log("[!] 无法读取目标窗口矩形"); return 2; }

        bool exact = false;
        if (o.Wallpaper != "")
        {
            // A specific wallpaper was requested, so a window still holding the name is
            // stale (it would show the previous wallpaper).  Clear it first.
            CloseStaleWallpaperWindows(o);
            if (!LaunchWallpaper(o, rc)) return 3;
            for (int i = 0; i < 80 && _wall == IntPtr.Zero; i++)
            {
                System.Threading.Thread.Sleep(250);
                _wall = FindWeWindow(o, out exact);
            }
        }
        else
        {
            _wall = FindWeWindow(o, out exact);       // attach mode: reuse what is open
            if (_wall != IntPtr.Zero && !IsWindowVisible(_wall))
                Log("[i] 接管的是一个隐藏的壁纸窗口（上次被强杀留下的），将重新显示。");
        }
        if (_wall == IntPtr.Zero)
        {
            Log("[!] 未找到 Wallpaper Engine 壁纸窗口。");
            Log("    请传入 --wallpaper \"...\\project.json\"，或自己先打开一个：");
            Log("    wallpaper64.exe -control openWallpaper -file \"...\" -playInWindow \"" + o.WeWindow + "\" -borderless");
            return 3;
        }
        Log(string.Format("[i] 壁纸窗口 0x{0:X}  类名={1}  标题={2}", _wall.ToInt64(), Cls(_wall), Text(_wall)));
        if (!exact)
            Log("[!] 按尺寸猜测而非按名称匹配 —— 若选错窗口请用 --we-window / --attach-title 指定。");

        _wndProc = delegate(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            switch (m)
            {
                case WM_TIMER:
                    if (w.ToInt64() == 1) { Sync(false); return IntPtr.Zero; }
                    if (w.ToInt64() == 2)          // host responsiveness watchdog
                    {
                        if (!_stopping && _target != IntPtr.Zero && IsWindow(_target))
                        {
                            if (TargetResponsive(_target, 600)) _hangStrikes = 0;
                            else if (++_hangStrikes >= 3)
                            {
                                Log("[!] 宿主窗口连续 3 次无响应 —— 自动还原并退出，避免你卡在无法操作的界面上。");
                                RestoreAll();
                                PostThreadMessageW(_mainThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                            }
                        }
                        return IntPtr.Zero;
                    }
                    return IntPtr.Zero;
                case WM_SET_HOST_ALPHA:             // live host opacity
                {
                    _liveHostAlpha = (byte)(w.ToInt64() & 0xFF);
                    IntPtr layered = _content != IntPtr.Zero && IsWindow(_content) ? _content
                                   : (_targetExSaved && _target != IntPtr.Zero && IsWindow(_target) ? _target : IntPtr.Zero);
                    if (layered != IntPtr.Zero) SetLayeredWindowAttributes(layered, 0, _liveHostAlpha, LWA_ALPHA);
                    return IntPtr.Zero;
                }
                case WM_SET_WALL_ALPHA:             // live wallpaper brightness
                    _liveWallAlpha = (byte)(w.ToInt64() & 0xFF);
                    if (_wall != IntPtr.Zero && IsWindow(_wall))
                        SetLayeredWindowAttributes(_wall, 0, _liveWallAlpha, LWA_ALPHA);
                    return IntPtr.Zero;
                case 0x0312:                        // WM_HOTKEY: emergency restore
                    Log("[i] 收到紧急还原热键 (Ctrl+Alt+Shift+W)。");
                    RestoreAll();
                    PostThreadMessageW(_mainThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                case WM_CLOSE: RestoreAll(); PostQuitMessage(0); return IntPtr.Zero;
                case WM_DESTROY: PostQuitMessage(0); return IntPtr.Zero;
                default: return DefWindowProcW(h, m, w, l);
            }
        };
        var wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = _wndProc;
        wc.hInstance = GetModuleHandleW(null);
        wc.lpszClassName = "WeCodexBgMsgCs";
        RegisterClassExW(ref wc);
        _msgWnd = CreateWindowExW(0, "WeCodexBgMsgCs", "we-codex-bg", 0, 0, 0, 0, 0,
                                  HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _ctrlHandler = delegate(uint type)
        {
            RestoreAll();
            PostThreadMessageW(_mainThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            System.Threading.Thread.Sleep(250);
            return true;
        };
        SetConsoleCtrlHandler(_ctrlHandler, true);
        AppDomain.CurrentDomain.ProcessExit += delegate { RestoreAll(); };
        AppDomain.CurrentDomain.UnhandledException += delegate { RestoreAll(); };

        var order = new List<Mode>();
        order.Add(o.Mode);
        if (o.Fallback)
            foreach (Mode m in new Mode[] { Mode.Alpha, Mode.Overlay, Mode.Composite, Mode.Embed })
                if (m != o.Mode) order.Add(m);

        bool up = false;
        foreach (Mode m in order)
        {
            _mode = m;
            _place = PlaceOf(m);
            Log("[i] 尝试模式 mode=" + m.ToString().ToLowerInvariant());
            PrepareWall();
            if (ApplyCompositing(o, m)) { up = true; break; }
            Log("[!] 模式 mode=" + m.ToString().ToLowerInvariant() + " 不可用，正在回退");
            UndoAttempt();
        }
        if (!up)
        {
            Log("[!] 没有可用的模式。");
            CloseLaunchedWallpaper();
            return 4;
        }
        Sync(true);

        uint pid;
        GetWindowThreadProcessId(_target, out pid);
        _winEvent = delegate(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint tid, uint time)
        {
            switch (ev)
            {
                case EVENT_OBJECT_DESTROY:
                case EVENT_OBJECT_HIDE:
                    // in child placement the wallpaper dies with its parent: restore now
                    if (hwnd == _target && idObj == OBJID_WINDOW)
                    {
                        LogV("[hook] target gone");
                        RestoreAll();
                        PostThreadMessageW(_mainThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                    }
                    return;
                case EVENT_OBJECT_LOCATIONCHANGE:
                    if (hwnd != _target || idObj != OBJID_WINDOW) return;
                    Sync(false);
                    return;
                case EVENT_SYSTEM_FOREGROUND:
                case EVENT_SYSTEM_MOVESIZEEND:
                case EVENT_SYSTEM_MINIMIZESTART:
                    Sync(false);
                    return;
                case EVENT_SYSTEM_MINIMIZEEND:
                    Sync(hwnd == _target);
                    return;
                default:
                    return;
            }
        };
        IntPtr h1 = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                                    _winEvent, pid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        IntPtr h2 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero,
                                    _winEvent, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        IntPtr h3 = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero,
                                    _winEvent, pid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        SetTimer(_msgWnd, new IntPtr(1), (uint)(1000 / o.Fps), IntPtr.Zero);
        SetTimer(_msgWnd, new IntPtr(2), 1000, IntPtr.Zero);
        // MOD_ALT=1 | MOD_CONTROL=2 | MOD_SHIFT=4 | MOD_NOREPEAT=0x4000
        bool hotkey = RegisterHotKey(_msgWnd, 1, 1 | 2 | 4 | 0x4000, (uint)'W');

        Log("[i] 运行中 (mode=" + _mode.ToString().ToLowerInvariant() + ")。在控制台按 Ctrl+C 可停止并恢复。");
        if (hotkey) Log("[i] 紧急还原热键：Ctrl+Alt+Shift+W（界面卡住时随时可用）。");
        else Log("[!] 紧急还原热键注册失败（可能被别的程序占用）。");

        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        KillTimer(_msgWnd, new IntPtr(1));
        KillTimer(_msgWnd, new IntPtr(2));
        if (hotkey) UnregisterHotKey(_msgWnd, 1);
        if (h1 != IntPtr.Zero) UnhookWinEvent(h1);
        if (h2 != IntPtr.Zero) UnhookWinEvent(h2);
        if (h3 != IntPtr.Zero) UnhookWinEvent(h3);
        RestoreAll();
        if (_msgWnd != IntPtr.Zero) DestroyWindow(_msgWnd);
        return 0;
    }
}
