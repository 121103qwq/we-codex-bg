// we_codex_bg.cpp  --  Wallpaper Engine live wallpaper as the background of the
// Codex / ChatGPT desktop window on Windows.
//
// No .pkg parsing, no renderer re-implementation: Wallpaper Engine renders into
// its own window (wallpaper64.exe -control openWallpaper -playInWindow ...) and
// this helper only does window plumbing:
//
//   composite (default) : SetParent the wallpaper window INTO the Codex window as
//                         the bottom-most child, then make only the content child
//                         window (the one that paints the page) layered with
//                         LWA_ALPHA.  Window frame / title bar stay opaque, the
//                         animation shows through the UI, and move / resize /
//                         maximize / minimize come for free from the parent.
//   embed               : same embedding, no transparency at all (pure plumbing
//                         test; also the right mode if the host ever paints a
//                         transparent background).
//   alpha               : wallpaper stays a top-level window pinned immediately
//                         BELOW the Codex window, and the whole Codex window is
//                         made translucent.  Works with any host, single HWND or
//                         not, but the text fades too.
//   overlay             : wallpaper pinned immediately ABOVE the Codex window as a
//                         click-through translucent film.  Nothing about the Codex
//                         window is modified at all - the safest fallback.
//
// Every style / parent change is restored on exit (Ctrl+C, console close, target
// hidden or closed, unhandled crash).  `--restore` cleans up after a hard kill.
//
// Build: build.bat   (MSVC / clang-cl / MinGW-w64, x64)

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS 1

#include <windows.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <cstdio>
#include <cstdlib>
#include <cstdarg>
#include <cwchar>
#include <cwctype>

#ifdef _MSC_VER
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#endif

#ifndef DWMWA_CLOAKED
#define DWMWA_CLOAKED 14
#endif

// ----------------------------------------------------------------- logging ---

static bool g_verbose = false;

static std::string ToUtf8(const std::wstring& w) {
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s((size_t)n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}
static void Out(const wchar_t* fmt, ...) {
    wchar_t buf[4096] = L"";
    va_list ap;
    va_start(ap, fmt);
    if (vswprintf(buf, 4096, fmt, ap) < 0) buf[4095] = L'\0';   // truncation-safe
    va_end(ap);
    fputs(ToUtf8(buf).c_str(), stdout);
    fputc('\n', stdout);
    fflush(stdout);
}
#define LOGV(...) do { if (g_verbose) Out(__VA_ARGS__); } while (0)

// -------------------------------------------------------------- small utils ---

static std::wstring WndText(HWND h) {
    wchar_t buf[512] = L"";
    GetWindowTextW(h, buf, 512);
    return buf;
}
static std::wstring WndClass(HWND h) {
    wchar_t buf[256] = L"";
    GetClassNameW(h, buf, 256);
    return buf;
}
static std::wstring Lower(std::wstring s) {
    for (auto& c : s) c = (wchar_t)towlower(c);
    return s;
}
static bool Contains(const std::wstring& hay, const std::wstring& needle) {
    if (needle.empty()) return true;
    return Lower(hay).find(Lower(needle)) != std::wstring::npos;
}
static bool IsCloaked(HWND h) {
    int cloaked = 0;
    if (SUCCEEDED(DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))))
        return cloaked != 0;
    return false;
}
static bool SameRect(const RECT& a, const RECT& b) {
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}
static LONG Area(const RECT& r) { return (r.right - r.left) * (r.bottom - r.top); }
static std::wstring ExeOfPid(DWORD pid) {
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!p) return L"";
    wchar_t path[MAX_PATH] = L"";
    DWORD n = MAX_PATH;
    std::wstring out;
    if (QueryFullProcessImageNameW(p, 0, path, &n)) out.assign(path, n);
    CloseHandle(p);
    return out;
}
static std::wstring BaseName(const std::wstring& path) {
    size_t p = path.find_last_of(L"\\/");
    return p == std::wstring::npos ? path : path.substr(p + 1);
}
static bool FileExists(const std::wstring& p) {
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

// ------------------------------------------------------------------ options ---

enum class Mode { Composite, Embed, Alpha, Overlay };
enum class Place { ChildBottom, TopLevelBelow, TopLevelAbove };  // wallpaper position

static const wchar_t* ModeName(Mode m) {
    switch (m) {
    case Mode::Composite: return L"composite";
    case Mode::Embed:     return L"embed";
    case Mode::Alpha:     return L"alpha";
    default:              return L"overlay";
    }
}
static Place PlaceOf(Mode m) {
    switch (m) {
    case Mode::Composite:
    case Mode::Embed: return Place::ChildBottom;
    case Mode::Alpha: return Place::TopLevelBelow;
    default:          return Place::TopLevelAbove;
    }
}

struct Options {
    std::wstring targetTitle, targetClass, targetExe;
    DWORD        targetPid = 0;
    std::wstring weExe, wallpaper, attachTitle;
    std::wstring weWindow     = L"CodexWallpaperHost";
    std::wstring contentClass;                 // which child window to fade (composite)
    // alpha is the default: it only layers the TOP-LEVEL window, which every host
    // tolerates.  composite touches the content child and is unsafe on Chromium.
    Mode  mode       = Mode::Alpha;
    BYTE  alpha      = 205;                    // composite/alpha: host opacity
    BYTE  filmAlpha  = 70;                     // overlay: wallpaper opacity
    bool  clientOnly = true;
    int   fps        = 30;
    int   round      = 0;
    bool  keepWe     = false;
    bool  fallback   = true;
    bool  listOnly = false, treeOnly = false, restoreOnly = false;
};

// -------------------------------------------------------------------- state ---

// styles/ex-styles this tool adds to the wallpaper window (cleared again on repair)
static const LONG_PTR kWallExAdded = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW |
                                     WS_EX_TRANSPARENT | WS_EX_LAYERED;

struct State {
    HWND target = nullptr, wall = nullptr, content = nullptr, msgWnd = nullptr;

    bool     targetExSaved = false;   LONG_PTR targetEx = 0;
    bool     contentExSaved = false;  LONG_PTR contentEx = 0;
    bool     wallSaved = false;       LONG_PTR wallStyle = 0, wallEx = 0;
    HWND     wallParent = nullptr;
    bool     weLaunched = false;

    RECT lastRect{ 0, 0, 0, 0 };
    bool wallHidden = false;
    bool restored = true;             // false once something has actually been changed

    Mode  mode = Mode::Composite;
    Place place = Place::ChildBottom;
    bool  clientOnly = true, keepWe = false;
    int   round = 0;
};
static State g;
static DWORD g_mainThread = 0;
static volatile bool g_stopping = false;   // set by RestoreAll: Sync must stop touching windows

// ------------------------------------------------------------- window lookup ---

struct WinInfo { HWND hwnd; DWORD pid; std::wstring title, cls, exe; RECT rc; };

static BOOL CALLBACK CollectProc(HWND h, LPARAM lp) {
    auto* v = reinterpret_cast<std::vector<WinInfo>*>(lp);
    if (!IsWindowVisible(h)) return TRUE;
    RECT rc{};
    if (!GetWindowRect(h, &rc)) return TRUE;
    if (rc.right - rc.left < 80 || rc.bottom - rc.top < 60) return TRUE;
    if (IsCloaked(h)) return TRUE;
    WinInfo wi;
    wi.hwnd = h; wi.rc = rc;
    GetWindowThreadProcessId(h, &wi.pid);
    wi.title = WndText(h);
    wi.cls   = WndClass(h);
    wi.exe   = BaseName(ExeOfPid(wi.pid));
    v->push_back(wi);
    return TRUE;
}
static std::vector<WinInfo> TopLevelWindows() {
    std::vector<WinInfo> v;
    EnumWindows(CollectProc, reinterpret_cast<LPARAM>(&v));
    return v;
}
static bool LooksLikeWeProcess(const std::wstring& exe) {
    std::wstring e = Lower(exe);
    return e == L"wallpaper64.exe" || e == L"wallpaper32.exe" ||
           e == L"webwallpaper64.exe" || e == L"webwallpaper32.exe" ||
           e == L"wallpaperwindows.exe" ||
           e == L"wallpaperservice64_engine.exe" || e == L"wallpaperservice32_engine.exe";
}

static HWND FindTarget(const Options& o) {
    bool useDefaults = o.targetTitle.empty() && o.targetClass.empty() &&
                       o.targetExe.empty() && o.targetPid == 0;
    HWND fg = GetForegroundWindow();
    HWND best = nullptr;
    LONG bestScore = 0;
    for (auto& w : TopLevelWindows()) {
        if (w.hwnd == g.msgWnd) continue;
        if (LooksLikeWeProcess(w.exe)) continue;
        if (o.targetPid && w.pid != o.targetPid) continue;
        if (!o.targetExe.empty() && Lower(w.exe) != Lower(o.targetExe)) continue;
        if (!o.targetClass.empty() && !Contains(w.cls, o.targetClass)) continue;
        if (!o.targetTitle.empty() && !Contains(w.title, o.targetTitle)) continue;
        if (useDefaults) {
            std::wstring e = Lower(w.exe);
            bool byExe = e == L"chatgpt.exe" || e == L"codex.exe" ||
                         e == L"openai.exe"  || e == L"chatgpt-desktop.exe";
            bool byTitle = Contains(w.title, L"codex") || Contains(w.title, L"chatgpt");
            if (!byExe && !byTitle) continue;
        }
        LONG score = Area(w.rc) + (w.hwnd == fg ? 1 : 0);
        if (score > bestScore) { bestScore = score; best = w.hwnd; }
    }
    return best;
}

// The wallpaper window created by `-playInWindow <name>`.  `exact` reports whether
// it was identified by name or only guessed by size.
//
// Name matching deliberately looks at HIDDEN windows too.  PrepareWallWindow()
// hides the window while it restyles it, so a hard-killed run leaves a hidden
// window still holding the name - and Wallpaper Engine will then silently refuse
// to create another one with that same name, wedging every later run.
struct WeScan { const Options* o; HWND named; HWND best; LONG bestArea; };

static BOOL CALLBACK WeScanProc(HWND h, LPARAM lp) {
    auto* x = reinterpret_cast<WeScan*>(lp);
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (!LooksLikeWeProcess(BaseName(ExeOfPid(pid)))) return TRUE;

    std::wstring title = WndText(h);
    if (!x->o->weWindow.empty() && Lower(title) == Lower(x->o->weWindow)) { x->named = h; return FALSE; }
    if (!x->o->attachTitle.empty() && Contains(title, x->o->attachTitle)) { x->named = h; return FALSE; }
    if (Lower(title) == L"wallpaper engine") return TRUE;          // WE's own UI

    if (!IsWindowVisible(h) || IsCloaked(h)) return TRUE;          // guess-by-size: visible only
    RECT rc{};
    if (!GetWindowRect(h, &rc)) return TRUE;
    if (rc.right - rc.left < 80 || rc.bottom - rc.top < 60) return TRUE;
    LONG a = Area(rc);
    if (a > x->bestArea) { x->bestArea = a; x->best = h; }
    return TRUE;
}

static HWND FindWeWindow(const Options& o, bool* exact = nullptr) {
    WeScan x{ &o, nullptr, nullptr, 0 };
    EnumWindows(WeScanProc, reinterpret_cast<LPARAM>(&x));
    if (exact) *exact = (x.named != nullptr);
    return x.named ? x.named : x.best;
}

// Close any wallpaper window still holding our -playInWindow name, and wait for it
// to actually go away, so WE will hand out that name again.
static int CloseStaleWallpaperWindows(const Options& o) {
    if (o.weWindow.empty()) return 0;
    int closed = 0;
    for (int pass = 0; pass < 20; ++pass) {
        WeScan x{ &o, nullptr, nullptr, 0 };
        EnumWindows(WeScanProc, reinterpret_cast<LPARAM>(&x));
        if (!x.named) break;
        if (pass == 0) {
            Out(L"[i] 发现残留的壁纸窗口 0x%p（名称 %ls），先关掉它 —— "
                L"否则 Wallpaper Engine 不会再用这个名字开新窗口。",
                (void*)x.named, o.weWindow.c_str());
        }
        PostMessageW(x.named, WM_CLOSE, 0, 0);
        ++closed;
        Sleep(250);
    }
    return closed;
}

// Steam is frequently NOT under Program Files, so ask the registry first and then
// follow libraryfolders.vdf to the libraries on other drives.
static std::wstring RegString(HKEY hive, const wchar_t* key, const wchar_t* name) {
    for (DWORD flag : { KEY_WOW64_64KEY, KEY_WOW64_32KEY }) {
        HKEY h = nullptr;
        if (RegOpenKeyExW(hive, key, 0, KEY_QUERY_VALUE | flag, &h) != ERROR_SUCCESS) continue;
        wchar_t buf[MAX_PATH] = L"";
        DWORD cb = sizeof(buf), type = 0;
        LSTATUS st = RegQueryValueExW(h, name, nullptr, &type, (LPBYTE)buf, &cb);
        RegCloseKey(h);
        if (st == ERROR_SUCCESS && type == REG_SZ) return buf;
    }
    return L"";
}
static std::wstring ReadFileText(const std::wstring& path) {
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return L"";
    std::string bytes;
    char buf[4096];
    DWORD n = 0;
    while (ReadFile(f, buf, sizeof(buf), &n, nullptr) && n > 0) bytes.append(buf, n);
    CloseHandle(f);
    if (bytes.empty()) return L"";
    int need = MultiByteToWideChar(CP_UTF8, 0, bytes.data(), (int)bytes.size(), nullptr, 0);
    std::wstring w((size_t)need, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, bytes.data(), (int)bytes.size(), &w[0], need);
    return w;
}
static std::vector<std::wstring> SteamRoots() {
    std::vector<std::wstring> roots;
    auto add = [&roots](std::wstring p) {
        if (p.empty()) return;
        for (auto& c : p) if (c == L'/') c = L'\\';
        while (!p.empty() && p.back() == L'\\') p.pop_back();
        if (p.empty()) return;
        for (auto& r : roots) if (Lower(r) == Lower(p)) return;
        roots.push_back(p);
    };
    add(RegString(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath"));
    add(RegString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Valve\\Steam", L"InstallPath"));
    add(L"C:\\Program Files (x86)\\Steam");
    add(L"C:\\Program Files\\Steam");
    add(L"D:\\Steam");
    add(L"D:\\SteamLibrary");
    add(L"E:\\Steam");
    add(L"E:\\SteamLibrary");

    for (size_t i = 0; i < roots.size(); ++i) {
        for (const wchar_t* rel : { L"\\steamapps\\libraryfolders.vdf", L"\\config\\libraryfolders.vdf" }) {
            std::wstring vdf = ReadFileText(roots[i] + rel);
            if (vdf.empty()) continue;
            const std::wstring low = Lower(vdf);
            size_t k = 0;
            while ((k = low.find(L"\"path\"", k)) != std::wstring::npos) {
                k += 6;
                size_t q1 = vdf.find(L'"', k);
                if (q1 == std::wstring::npos) break;
                size_t q2 = vdf.find(L'"', q1 + 1);
                if (q2 == std::wstring::npos) break;
                std::wstring p = vdf.substr(q1 + 1, q2 - q1 - 1);
                for (size_t d = p.find(L"\\\\"); d != std::wstring::npos; d = p.find(L"\\\\", d + 1))
                    p.erase(d, 1);
                add(p);
                k = q2 + 1;
            }
        }
    }
    return roots;
}

static std::wstring FindWeExe(const Options& o) {
    if (!o.weExe.empty()) return o.weExe;
    for (auto& w : TopLevelWindows()) {                     // ask a running instance
        if (!LooksLikeWeProcess(w.exe)) continue;
        std::wstring full = ExeOfPid(w.pid);
        if (full.empty()) continue;
        std::wstring dir = full.substr(0, full.find_last_of(L"\\/") + 1);
        if (FileExists(dir + L"wallpaper64.exe")) return dir + L"wallpaper64.exe";
        if (FileExists(dir + L"wallpaper32.exe")) return dir + L"wallpaper32.exe";
        return full;
    }
    for (const std::wstring& r : SteamRoots()) {
        std::wstring dir = r + L"\\steamapps\\common\\wallpaper_engine\\";
        std::wstring p = dir + L"wallpaper64.exe";
        if (FileExists(p)) return p;
        p = dir + L"wallpaper32.exe";
        if (FileExists(p)) return p;
    }
    return L"";
}

// ------------------------------------------------------------- geometry sync ---

static bool TargetRectOnScreen(HWND t, bool clientOnly, RECT& out) {
    if (!IsWindow(t)) return false;
    if (!clientOnly) return GetWindowRect(t, &out) != 0;
    RECT rc{};
    if (!GetClientRect(t, &rc)) return false;
    POINT a{ rc.left, rc.top }, b{ rc.right, rc.bottom };
    if (!ClientToScreen(t, &a) || !ClientToScreen(t, &b)) return false;
    out.left = a.x; out.top = a.y; out.right = b.x; out.bottom = b.y;
    return true;
}
// same rect but expressed in the target's client coordinates (for child placement)
static bool TargetRectInParent(HWND t, bool clientOnly, RECT& out) {
    if (clientOnly) return GetClientRect(t, &out) != 0;
    RECT wr{};
    if (!GetWindowRect(t, &wr)) return false;
    POINT p{ wr.left, wr.top };
    if (!ScreenToClient(t, &p)) return false;
    out.left = p.x; out.top = p.y;
    out.right = p.x + (wr.right - wr.left);
    out.bottom = p.y + (wr.bottom - wr.top);
    return true;
}
static void ApplyRoundedCorners(HWND h, int w, int ht, int radius) {
    if (radius <= 0) return;
    HRGN rgn = CreateRoundRectRgn(0, 0, w + 1, ht + 1, radius * 2, radius * 2);
    if (!rgn) return;
    if (!SetWindowRgn(h, rgn, TRUE)) DeleteObject(rgn);   // system owns it on success
}

static void Sync(bool force) {
    if (g_stopping) return;
    if (!g.target || !IsWindow(g.target) || !g.wall || !IsWindow(g.wall)) {
        PostThreadMessageW(g_mainThread, WM_QUIT, 0, 0);
        return;
    }
    const bool asChild = (g.place == Place::ChildBottom);

    if (!asChild) {   // a child window is hidden together with its parent already
        bool visible = IsWindowVisible(g.target) && !IsIconic(g.target) && !IsCloaked(g.target);
        if (!visible) {
            if (!g.wallHidden) {
                ShowWindow(g.wall, SW_HIDE);
                g.wallHidden = true;
                LOGV(L"[sync] target not visible -> wallpaper hidden");
            }
            return;
        }
        if (g.wallHidden) { g.wallHidden = false; force = true; }
    }

    RECT r{};
    if (asChild) { if (!TargetRectInParent(g.target, g.clientOnly, r)) return; }
    else         { if (!TargetRectOnScreen(g.target, g.clientOnly, r)) return; }
    int w = r.right - r.left, h = r.bottom - r.top;
    if (w <= 0 || h <= 0) return;

    bool sizeChanged = (w != g.lastRect.right - g.lastRect.left) ||
                       (h != g.lastRect.bottom - g.lastRect.top);
    bool rectChanged = !SameRect(r, g.lastRect);

    HWND insertAfter = nullptr;
    bool zBad = false;
    switch (g.place) {
    case Place::ChildBottom:
        insertAfter = HWND_BOTTOM;
        zBad = (GetWindow(g.wall, GW_HWNDNEXT) != nullptr);        // must be last sibling
        break;
    case Place::TopLevelBelow:
        insertAfter = g.target;
        zBad = (GetWindow(g.target, GW_HWNDNEXT) != g.wall);       // directly below target
        break;
    case Place::TopLevelAbove: {
        zBad = (GetWindow(g.wall, GW_HWNDNEXT) != g.target);       // directly above target
        HWND above = GetWindow(g.target, GW_HWNDPREV);
        if (above == g.wall) above = GetWindow(g.wall, GW_HWNDPREV);
        insertAfter = above ? above : HWND_TOP;
        break;
    }
    }

    if (force || rectChanged || zBad) {
        UINT flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
        if (!zBad && !force) flags |= SWP_NOZORDER;         // don't thrash the z-order
        SetWindowPos(g.wall, insertAfter, r.left, r.top, w, h, flags);
        g.lastRect = r;
        if (g.round > 0 && (sizeChanged || force)) ApplyRoundedCorners(g.wall, w, h, g.round);
    }
    if (!asChild && force) ShowWindow(g.wall, SW_SHOWNOACTIVATE);
}

// -------------------------------------------------------------- content child ---

// Largest visible direct child of the target: for Chromium / WebView2 / WinUI
// hosts this is the window that actually paints the page.  Our own wallpaper
// child must never be picked.
struct PickCtx { HWND parent; const std::wstring* flt; HWND best; LONG bestArea; };
static BOOL CALLBACK PickChildProc(HWND c, LPARAM lp) {
    auto* x = reinterpret_cast<PickCtx*>(lp);
    if (c == g.wall) return TRUE;
    if (!x->flt->empty()) {                       // explicit class filter: any depth
        if (!Contains(WndClass(c), *x->flt)) return TRUE;
    } else if (GetParent(c) != x->parent) {
        return TRUE;                              // otherwise: direct children only
    }
    if (!IsWindowVisible(c)) return TRUE;
    RECT rc{};
    if (!GetWindowRect(c, &rc)) return TRUE;
    LONG a = Area(rc);
    if (a > x->bestArea) { x->bestArea = a; x->best = c; }
    return TRUE;
}
static HWND PickContentChild(HWND target, const std::wstring& clsFilter) {
    PickCtx ctx{ target, &clsFilter, nullptr, 0 };
    EnumChildWindows(target, PickChildProc, reinterpret_cast<LPARAM>(&ctx));
    return ctx.best;
}

// -------------------------------------------------------------- style surgery ---

static void ForceRepaint(HWND h) {
    SetWindowPos(h, nullptr, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    RedrawWindow(h, nullptr, nullptr,
                 RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
}

// WS_EX_TRANSPARENT is NOT inherited by child windows, so a wallpaper window whose
// renderer lives in a child HWND would still swallow clicks.  Stamp the whole tree.
static BOOL CALLBACK ClickThroughProc(HWND c, LPARAM) {
    LONG_PTR ex = GetWindowLongPtrW(c, GWL_EXSTYLE);
    SetWindowLongPtrW(c, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    return TRUE;
}
static void MakeClickThroughTree(HWND root) {
    LONG_PTR ex = GetWindowLongPtrW(root, GWL_EXSTYLE);
    SetWindowLongPtrW(root, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    EnumChildWindows(root, ClickThroughProc, 0);
}

// Safety net: if the host stops answering messages while we are attached, undo
// everything rather than leaving the user with a window they cannot click.
static int g_hangStrikes = 0;
static bool TargetResponsive(HWND t, UINT timeoutMs) {
    DWORD_PTR res = 0;
    return SendMessageTimeoutW(t, WM_NULL, 0, 0,
                               SMTO_ABORTIFHUNG | SMTO_NORMAL, timeoutMs, &res) != 0;
}

static void PrepareWallWindow() {
    g.wallStyle = GetWindowLongPtrW(g.wall, GWL_STYLE);
    g.wallEx    = GetWindowLongPtrW(g.wall, GWL_EXSTYLE);
    g.wallParent = GetAncestor(g.wall, GA_PARENT);          // real parent, not the owner
    if (g.wallParent == GetDesktopWindow()) g.wallParent = nullptr;
    g.wallSaved = true;
    g.restored  = false;

    const bool asChild = (g.place == Place::ChildBottom);

    ShowWindow(g.wall, SW_HIDE);          // so the taskbar button really disappears

    LONG_PTR st = g.wallStyle & ~WS_VISIBLE;   // it is hidden right now
    st &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX |
            WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
    st |= WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
    if (asChild) { st &= ~WS_POPUP; st |= WS_CHILD; }
    else         { st &= ~WS_CHILD;  st |= WS_POPUP; }
    SetWindowLongPtrW(g.wall, GWL_STYLE, st);

    LONG_PTR ex = g.wallEx;
    ex &= ~(WS_EX_APPWINDOW | WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE |
            WS_EX_WINDOWEDGE | WS_EX_STATICEDGE);
    ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;  // never eat clicks
    SetWindowLongPtrW(g.wall, GWL_EXSTYLE, ex);

    if (asChild) SetParent(g.wall, g.target);
    else SetWindowPos(g.wall, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    MakeClickThroughTree(g.wall);          // the wallpaper must never eat a click
    SetWindowPos(g.wall, nullptr, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    ShowWindow(g.wall, SW_SHOWNOACTIVATE);
    g.wallHidden = false;
    LOGV(L"[wall] style %llx->%llx ex %llx->%llx parent=0x%p",
         (unsigned long long)g.wallStyle, (unsigned long long)st,
         (unsigned long long)g.wallEx, (unsigned long long)ex, (void*)g.wallParent);
}

static bool MakeLayered(HWND h, BYTE alpha) {
    LONG_PTR ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
    SetLastError(0);
    SetWindowLongPtrW(h, GWL_EXSTYLE, ex | WS_EX_LAYERED);
    if (DWORD e = GetLastError()) LOGV(L"[layer] SetWindowLongPtr err=%lu", e);
    if (!SetLayeredWindowAttributes(h, 0, alpha, LWA_ALPHA)) {
        Out(L"[!] SetLayeredWindowAttributes(0x%p) 失败，err=%lu", (void*)h, GetLastError());
        return false;
    }
    ForceRepaint(h);
    return true;
}

// Apply the compositing part of a mode.  Returns false if the OS refused.
static bool ApplyCompositing(const Options& o, Mode m) {
    switch (m) {
    case Mode::Embed:
        return true;                                    // plumbing only

    case Mode::Composite: {
        HWND c = PickContentChild(g.target, o.contentClass);
        if (!c) {
            Out(L"[!] 合成模式：未找到内容子窗口 —— 请用 --tree 查看后");
            Out(L"    传入 --content-class <类名片段>，或改用 --mode alpha。");
            return false;
        }
        // A content window that ALREADY has WS_EX_TRANSPARENT must never get
        // WS_EX_LAYERED on top: the combination makes it truly click-through, so
        // the host renders normally but receives no mouse input at all.  Chromium
        // /Electron hosts (Codex, ChatGPT, VS Code) ship Chrome_RenderWidgetHostHWND
        // with WS_EX_TRANSPARENT already set - layering it kills the UI, and can
        // take the renderer down with it.  Verified on an Electron host.
        LONG_PTR cex = GetWindowLongPtrW(c, GWL_EXSTYLE);
        if (cex & WS_EX_TRANSPARENT) {
            Out(L"[!] 合成模式不可用：内容窗口 %ls 本身已带 WS_EX_TRANSPARENT，", WndClass(c).c_str());
            Out(L"    再叠加 WS_EX_LAYERED 会让宿主界面完全收不到鼠标输入（Electron/Chromium 宿主的已知问题）。");
            Out(L"    自动改用更安全的模式。");
            return false;
        }
        g.content = c;
        g.contentEx = GetWindowLongPtrW(c, GWL_EXSTYLE);
        g.contentExSaved = true;
        g.restored = false;
        Out(L"[i] 内容窗口 0x%p  类名=%ls  不透明度=%u", (void*)c, WndClass(c).c_str(), o.alpha);
        return MakeLayered(c, o.alpha);
    }
    case Mode::Alpha:
        g.targetEx = GetWindowLongPtrW(g.target, GWL_EXSTYLE);
        g.targetExSaved = true;
        g.restored = false;
        return MakeLayered(g.target, o.alpha);

    default:   // Overlay: fade the wallpaper window itself, touch nothing else
        return MakeLayered(g.wall, o.filmAlpha);
    }
}

// ------------------------------------------------------------------- restore ---

static void RestoreAll() {
    if (g.restored) return;
    g_stopping = true;                 // stop Sync() from touching windows again
    g.restored = true;

    if (g.contentExSaved && g.content && IsWindow(g.content)) {
        SetWindowLongPtrW(g.content, GWL_EXSTYLE, g.contentEx);
        ForceRepaint(g.content);
    }
    if (g.targetExSaved && g.target && IsWindow(g.target)) {
        SetWindowLongPtrW(g.target, GWL_EXSTYLE, g.targetEx);
        ForceRepaint(g.target);
    }
    if (g.wallSaved && g.wall && IsWindow(g.wall)) {
        SetWindowRgn(g.wall, nullptr, TRUE);
        // restore the styles first, then un-parent: a WS_CHILD window whose parent
        // is the desktop is a broken state
        SetWindowLongPtrW(g.wall, GWL_STYLE, g.wallStyle);
        SetWindowLongPtrW(g.wall, GWL_EXSTYLE, g.wallEx);
        if (g.place == Place::ChildBottom) SetParent(g.wall, g.wallParent);
        SetWindowPos(g.wall, nullptr, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (g.weLaunched && !g.keepWe) PostMessageW(g.wall, WM_CLOSE, 0, 0);
        else                           ShowWindow(g.wall, SW_SHOWNOACTIVATE);
    }
    if (g.target && IsWindow(g.target)) ForceRepaint(g.target);
    Out(L"[i] 已恢复窗口原始状态。");
}

static BOOL WINAPI CtrlHandler(DWORD type) {
    switch (type) {
    case CTRL_C_EVENT: case CTRL_BREAK_EVENT: case CTRL_CLOSE_EVENT:
    case CTRL_LOGOFF_EVENT: case CTRL_SHUTDOWN_EVENT:
        RestoreAll();
        PostThreadMessageW(g_mainThread, WM_QUIT, 0, 0);
        Sleep(250);
        return TRUE;
    default:
        return FALSE;
    }
}
static LONG WINAPI CrashHandler(EXCEPTION_POINTERS*) { RestoreAll(); return EXCEPTION_EXECUTE_HANDLER; }
static void AtExitRestore() { RestoreAll(); }

// ------- repair after a hard kill: un-layer the subtree, evict WE child windows --

static BOOL CALLBACK CollectAllProc(HWND ch, LPARAM lp) {
    reinterpret_cast<std::vector<HWND>*>(lp)->push_back(ch);
    return TRUE;
}
static void RestoreOnly(const Options& o) {
    int stale = CloseStaleWallpaperWindows(o);   // always safe, even with no target
    if (stale) Out(L"[i] 已清理 %d 个残留的壁纸窗口。", stale);

    HWND t = FindTarget(o);
    if (!t) { Out(L"[!] 未找到目标窗口（残留壁纸窗口已清理）。"); return; }

    std::vector<HWND> all{ t };
    EnumChildWindows(t, CollectAllProc, reinterpret_cast<LPARAM>(&all));  // snapshot first

    int fixed = 0;
    for (HWND h : all) {
        if (!IsWindow(h)) continue;
        LONG_PTR ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
        DWORD pid = 0;
        GetWindowThreadProcessId(h, &pid);
        bool isWe = (h != t) && LooksLikeWeProcess(BaseName(ExeOfPid(pid)));

        if (isWe) {   // a wallpaper window still parented into the target: evict it
            SetWindowRgn(h, nullptr, TRUE);
            SetWindowLongPtrW(h, GWL_EXSTYLE, ex & ~kWallExAdded);
            LONG_PTR st = GetWindowLongPtrW(h, GWL_STYLE);
            SetWindowLongPtrW(h, GWL_STYLE, (st & ~WS_CHILD) | WS_POPUP);
            SetParent(h, nullptr);
            SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            ShowWindow(h, SW_SHOWNOACTIVATE);   // stay findable / closable
            ++fixed;
        } else if (ex & WS_EX_LAYERED) {
            SetWindowLongPtrW(h, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
            ForceRepaint(h);
            ++fixed;
        }
    }
    Out(L"[i] 已修复 0x%p 下的 %d 个窗口。", (void*)t, fixed);
}

// ---------------------------------------------------------------- event hooks ---

static void CALLBACK WinEventProc(HWINEVENTHOOK, DWORD event, HWND hwnd,
                                  LONG idObject, LONG, DWORD, DWORD) {
    switch (event) {
    case EVENT_OBJECT_DESTROY:
    case EVENT_OBJECT_HIDE:
        // The target is going away.  In child placement the wallpaper window would
        // be destroyed with its parent, so restore right now, while it still exists.
        if (hwnd == g.target && idObject == OBJID_WINDOW) {
            LOGV(L"[hook] target %ls", event == EVENT_OBJECT_HIDE ? L"hidden" : L"destroyed");
            RestoreAll();
            PostThreadMessageW(g_mainThread, WM_QUIT, 0, 0);
        }
        return;

    case EVENT_OBJECT_LOCATIONCHANGE:
        if (hwnd != g.target || idObject != OBJID_WINDOW) return;
        Sync(false);
        return;

    case EVENT_SYSTEM_FOREGROUND:
    case EVENT_SYSTEM_MOVESIZEEND:
    case EVENT_SYSTEM_MINIMIZESTART:
        Sync(false);
        return;

    case EVENT_SYSTEM_MINIMIZEEND:
        Sync(hwnd == g.target);
        return;

    default:
        return;                       // ignore menus, scrolling, drag&drop, ...
    }
}

#define TIMER_SYNC  1
#define TIMER_GUARD  2
#define HOTKEY_PANIC 1

static LRESULT CALLBACK MsgProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_TIMER:
        if (w == TIMER_SYNC) { Sync(false); return 0; }
        if (w == TIMER_GUARD) {                 // host responsiveness watchdog
            if (!g_stopping && g.target && IsWindow(g.target)) {
                if (TargetResponsive(g.target, 600)) {
                    g_hangStrikes = 0;
                } else if (++g_hangStrikes >= 3) {
                    Out(L"[!] 宿主窗口连续 3 次无响应 —— 自动还原并退出，避免你卡在无法操作的界面上。");
                    RestoreAll();
                    PostThreadMessageW(g_mainThread, WM_QUIT, 0, 0);
                }
            }
            return 0;
        }
        return 0;
    case WM_HOTKEY:
        if (w == HOTKEY_PANIC) {
            Out(L"[i] 收到紧急还原热键 (Ctrl+Alt+Shift+W)。");
            RestoreAll();
            PostThreadMessageW(g_mainThread, WM_QUIT, 0, 0);
        }
        return 0;
    case WM_CLOSE:           RestoreAll(); PostQuitMessage(0); return 0;
    case WM_DESTROY:         PostQuitMessage(0); return 0;
    default:                 return DefWindowProcW(h, m, w, l);
    }
}

// ---------------------------------------------------------------- diagnostics ---

static void PrintList() {
    Out(L"%-18ls %-7ls %-24ls %-30ls %ls", L"HWND", L"PID", L"EXE", L"CLASS", L"TITLE / RECT");
    for (auto& w : TopLevelWindows())
        Out(L"0x%-16p %-7lu %-24ls %-30ls %ls  [%ld,%ld %ldx%ld]",
            (void*)w.hwnd, w.pid, w.exe.c_str(), w.cls.c_str(), w.title.c_str(),
            w.rc.left, w.rc.top, w.rc.right - w.rc.left, w.rc.bottom - w.rc.top);
}

struct TreeCtx { HWND parent; int depth; };
static void PrintTree(HWND root, int depth);
static BOOL CALLBACK TreeProc(HWND ch, LPARAM lp) {
    auto* x = reinterpret_cast<TreeCtx*>(lp);
    if (GetParent(ch) == x->parent) PrintTree(ch, x->depth + 1);
    return TRUE;
}
static void PrintTree(HWND root, int depth) {
    RECT rc{};
    GetWindowRect(root, &rc);
    std::wstring pad((size_t)depth * 2, L' ');
    Out(L"%ls0x%p  %-34ls %-22ls [%ldx%ld]%ls", pad.c_str(), (void*)root,
        WndClass(root).c_str(), WndText(root).c_str(),
        rc.right - rc.left, rc.bottom - rc.top, IsWindowVisible(root) ? L"" : L"  (hidden)");
    TreeCtx c{ root, depth };
    EnumChildWindows(root, TreeProc, reinterpret_cast<LPARAM>(&c));
}

static void Usage() {
    Out(
L"we-codex-bg  --  Wallpaper Engine live wallpaper behind the Codex/ChatGPT window\n"
L"\n"
L"Usage: we-codex-bg.exe [options]\n"
L"\n"
L"Modes\n"
L"  --mode composite   embed the wallpaper as the bottom-most child window and fade\n"
L"                     only the content child window (frame stays opaque)  [default]\n"
L"  --mode embed       embed only, no transparency (plumbing test)\n"
L"  --mode alpha       wallpaper pinned below the window + whole window translucent\n"
L"  --mode overlay     wallpaper pinned above the window as a click-through film;\n"
L"                     the Codex window itself is never modified\n"
L"  --alpha 0-255      host opacity for composite / alpha (default 205)\n"
L"  --film 0-255       wallpaper opacity for overlay (default 70)\n"
L"  --content-class <s>  which child window class to fade in composite mode\n"
L"  --no-fallback      do not auto-fall back to the next mode when one fails\n"
L"\n"
L"Target window\n"
L"  --title <substr>   match window title (default: Codex / ChatGPT)\n"
L"  --class <substr>   match window class\n"
L"  --exe <name.exe>   match process image name\n"
L"  --pid <n>          match process id\n"
L"\n"
L"Wallpaper Engine\n"
L"  --we <path>        wallpaper64.exe (auto-detected if omitted)\n"
L"  --wallpaper <path> project.json / mp4 / gif ...; launches WE for you\n"
L"  --we-window <name> -playInWindow name (default CodexWallpaperHost)\n"
L"  --attach-title <s> attach to an already open WE window by title substring\n"
L"  --keep-we          leave the wallpaper window open on exit\n"
L"\n"
L"Geometry / misc\n"
L"  --full             cover the whole window instead of the client area\n"
L"  --round <px>       rounded corners for the wallpaper window (default 0)\n"
L"  --fps <n>          fallback poll rate (default 30)\n"
L"  --list             list top-level windows and exit\n"
L"  --tree             dump the target's child window tree and exit\n"
L"  --restore          undo leftovers from a hard-killed run and exit\n"
L"  -v                 verbose");
}

// ---------------------------------------------------------------------- main ---

static bool ParseArgs(int argc, wchar_t** argv, Options& o) {
    for (int i = 1; i < argc; ++i) {
        std::wstring a = argv[i];
        bool has = (i + 1 < argc);
        auto want = [&](const wchar_t* name) -> bool {
            if (has) return true;
            Out(L"[!] %ls needs a value", name);
            return false;
        };
        if (a == L"--title")             { if (!want(L"--title")) return false; o.targetTitle = argv[++i]; }
        else if (a == L"--class")        { if (!want(L"--class")) return false; o.targetClass = argv[++i]; }
        else if (a == L"--exe")          { if (!want(L"--exe")) return false; o.targetExe = argv[++i]; }
        else if (a == L"--pid")          { if (!want(L"--pid")) return false; o.targetPid = (DWORD)wcstoul(argv[++i], nullptr, 10); }
        else if (a == L"--we")           { if (!want(L"--we")) return false; o.weExe = argv[++i]; }
        else if (a == L"--wallpaper")    { if (!want(L"--wallpaper")) return false; o.wallpaper = argv[++i]; }
        else if (a == L"--we-window")    { if (!want(L"--we-window")) return false; o.weWindow = argv[++i]; }
        else if (a == L"--attach-title") { if (!want(L"--attach-title")) return false; o.attachTitle = argv[++i]; }
        else if (a == L"--content-class"){ if (!want(L"--content-class")) return false; o.contentClass = argv[++i]; }
        else if (a == L"--keep-we")      o.keepWe = true;
        else if (a == L"--no-fallback")  o.fallback = false;
        else if (a == L"--mode") {
            if (!want(L"--mode")) return false;
            std::wstring m = Lower(argv[++i]);
            if (m == L"composite")    o.mode = Mode::Composite;
            else if (m == L"embed")   o.mode = Mode::Embed;
            else if (m == L"alpha")   o.mode = Mode::Alpha;
            else if (m == L"overlay") o.mode = Mode::Overlay;
            else { Out(L"[!] unknown mode: %ls", m.c_str()); return false; }
        }
        else if (a == L"--alpha") { if (!want(L"--alpha")) return false; o.alpha = (BYTE)wcstoul(argv[++i], nullptr, 10); }
        else if (a == L"--film")  { if (!want(L"--film")) return false; o.filmAlpha = (BYTE)wcstoul(argv[++i], nullptr, 10); }
        else if (a == L"--fps")   { if (!want(L"--fps")) return false; o.fps = (int)wcstol(argv[++i], nullptr, 10); }
        else if (a == L"--round") { if (!want(L"--round")) return false; o.round = (int)wcstol(argv[++i], nullptr, 10); }
        else if (a == L"--full")     o.clientOnly = false;
        else if (a == L"--list")     o.listOnly = true;
        else if (a == L"--tree")     o.treeOnly = true;
        else if (a == L"--restore")  o.restoreOnly = true;
        else if (a == L"-v" || a == L"--verbose") g_verbose = true;
        else if (a == L"-h" || a == L"--help")    { Usage(); return false; }
        else { Out(L"[!] unknown argument: %ls", a.c_str()); Usage(); return false; }
    }
    if (o.fps < 1) o.fps = 1;
    if (o.fps > 120) o.fps = 120;
    return true;
}

static bool LaunchWallpaper(const Options& o, const RECT& rc) {
    std::wstring exe = FindWeExe(o);
    if (exe.empty()) {
        Out(L"[!] 未找到 wallpaper64.exe —— 请用 --we \"...\\wallpaper_engine\\wallpaper64.exe\" 指定");
        return false;
    }
    wchar_t cmd[2048] = L"";
    _snwprintf(cmd, 2047,
        L"\"%ls\" -control openWallpaper -file \"%ls\" -playInWindow \"%ls\" "
        L"-width %ld -height %ld -x %ld -y %ld -borderless",
        exe.c_str(), o.wallpaper.c_str(), o.weWindow.c_str(),
        rc.right - rc.left, rc.bottom - rc.top, rc.left, rc.top);
    cmd[2047] = 0;
    Out(L"[we] %ls", cmd);
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(exe.c_str(), cmd, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) {
        Out(L"[!] CreateProcess 失败 (err=%lu): %ls", GetLastError(), exe.c_str());
        return false;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    g.weLaunched = true;
    return true;
}

// Undo one failed attempt so the next mode can be tried from a clean state.
static void UndoAttempt() {
    bool keepWe = g.keepWe;
    g.keepWe = true;                       // never close the wallpaper between attempts
    RestoreAll();
    g.keepWe = keepWe;
    g_stopping = false;
    g.restored = true;
    g.content = nullptr;
    g.contentExSaved = g.targetExSaved = g.wallSaved = false;
    g.lastRect = RECT{ 0, 0, 0, 0 };
}
static void CloseLaunchedWallpaper() {
    if (g.weLaunched && !g.keepWe && g.wall && IsWindow(g.wall))
        PostMessageW(g.wall, WM_CLOSE, 0, 0);
}

int main() {
    SetConsoleOutputCP(CP_UTF8);

    using FnCtx = BOOL(WINAPI*)(HANDLE);        // per-monitor DPI v2: 1:1 coordinates
    if (HMODULE u32 = GetModuleHandleW(L"user32.dll"))
        if (auto fn = reinterpret_cast<FnCtx>(GetProcAddress(u32, "SetProcessDpiAwarenessContext")))
            fn((HANDLE)(INT_PTR)-4);

    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    Options o;
    bool argsOk = ParseArgs(argc, argv, o);
    LocalFree(argv);
    if (!argsOk) return 1;

    g_mainThread = GetCurrentThreadId();
    g.keepWe = o.keepWe;
    g.clientOnly = o.clientOnly;
    g.round = o.round;

    if (o.listOnly)    { PrintList(); return 0; }
    if (o.restoreOnly) { RestoreOnly(o); return 0; }

    g.target = FindTarget(o);
    if (!g.target) {
        Out(L"[!] 未找到 Codex/ChatGPT 窗口。请先运行 --list，再用 --pid / --title 指定。");
        return 2;
    }
    Out(L"[i] 目标窗口 0x%p  类名=%ls  标题=%ls", (void*)g.target,
        WndClass(g.target).c_str(), WndText(g.target).c_str());

    if (o.treeOnly) { PrintTree(g.target, 0); return 0; }

    RECT rc{};
    if (!TargetRectOnScreen(g.target, o.clientOnly, rc)) { Out(L"[!] 无法读取目标窗口矩形"); return 2; }

    bool exact = false;
    if (!o.wallpaper.empty()) {
        // A specific wallpaper was requested, so any window still holding the name is
        // stale (it would also be showing the previous wallpaper).  Clear it first.
        CloseStaleWallpaperWindows(o);
        if (!LaunchWallpaper(o, rc)) return 3;
        for (int i = 0; i < 80 && !g.wall; ++i) { Sleep(250); g.wall = FindWeWindow(o, &exact); }
    } else {
        g.wall = FindWeWindow(o, &exact);       // attach mode: reuse whatever is open
        if (g.wall && !IsWindowVisible(g.wall))
            Out(L"[i] 接管的是一个隐藏的壁纸窗口（上次被强杀留下的），将重新显示。");
    }
    if (!g.wall) {
        Out(L"[!] 未找到 Wallpaper Engine 壁纸窗口。");
        Out(L"    请传入 --wallpaper \"...\\project.json\"，或自己先打开一个：");
        Out(L"    wallpaper64.exe -control openWallpaper -file \"...\" -playInWindow \"%ls\" -borderless",
            o.weWindow.c_str());
        return 3;
    }
    Out(L"[i] 壁纸窗口 0x%p  类名=%ls  标题=%ls", (void*)g.wall,
        WndClass(g.wall).c_str(), WndText(g.wall).c_str());
    if (!exact)
        Out(L"[!] 按尺寸猜测而非按名称匹配 —— 若选错窗口请用 "
            L"--we-window / --attach-title 指定。");

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = MsgProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"WeCodexBgMsg";
    RegisterClassExW(&wc);
    g.msgWnd = CreateWindowExW(0, L"WeCodexBgMsg", L"we-codex-bg", 0, 0, 0, 0, 0,
                               HWND_MESSAGE, nullptr, wc.hInstance, nullptr);

    SetConsoleCtrlHandler(CtrlHandler, TRUE);
    SetUnhandledExceptionFilter(CrashHandler);
    atexit(AtExitRestore);

    std::vector<Mode> order{ o.mode };          // requested mode first, then safer ones
    if (o.fallback)
        for (Mode m : { Mode::Alpha, Mode::Overlay, Mode::Composite, Mode::Embed })
            if (m != o.mode) order.push_back(m);

    bool up = false;
    for (Mode m : order) {
        g.mode = m;
        g.place = PlaceOf(m);
        Out(L"[i] 尝试模式 mode=%ls", ModeName(m));
        PrepareWallWindow();
        if (ApplyCompositing(o, m)) { up = true; break; }
        Out(L"[!] 模式 mode=%ls 不可用，正在回退", ModeName(m));
        UndoAttempt();
    }
    if (!up) {
        Out(L"[!] 没有可用的模式。");
        CloseLaunchedWallpaper();
        return 4;
    }
    Sync(true);

    DWORD pid = 0;
    GetWindowThreadProcessId(g.target, &pid);
    HWINEVENTHOOK h1 = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                                       nullptr, WinEventProc, pid, 0,
                                       WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    HWINEVENTHOOK h2 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND,
                                       nullptr, WinEventProc, 0, 0,
                                       WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    HWINEVENTHOOK h3 = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE,
                                       nullptr, WinEventProc, pid, 0,
                                       WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    SetTimer(g.msgWnd, TIMER_SYNC, (UINT)(1000 / o.fps), nullptr);
    SetTimer(g.msgWnd, TIMER_GUARD, 1000, nullptr);
    // MOD_CONTROL|MOD_ALT|MOD_SHIFT = 0x2|0x1|0x4; MOD_NOREPEAT = 0x4000
    bool hotkey = RegisterHotKey(g.msgWnd, HOTKEY_PANIC, MOD_CONTROL | MOD_ALT | MOD_SHIFT | 0x4000, 'W') != 0;

    Out(L"[i] 运行中 (mode=%ls)。在控制台按 Ctrl+C 可停止并恢复。", ModeName(g.mode));
    if (hotkey) Out(L"[i] 紧急还原热键：Ctrl+Alt+Shift+W（界面卡住时随时可用）。");
    else        Out(L"[!] 紧急还原热键注册失败（可能被别的程序占用）。");

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    KillTimer(g.msgWnd, TIMER_SYNC);
    KillTimer(g.msgWnd, TIMER_GUARD);
    if (hotkey) UnregisterHotKey(g.msgWnd, HOTKEY_PANIC);
    if (h1) UnhookWinEvent(h1);
    if (h2) UnhookWinEvent(h2);
    if (h3) UnhookWinEvent(h3);
    RestoreAll();
    if (g.msgWnd) DestroyWindow(g.msgWnd);
    return 0;
}
