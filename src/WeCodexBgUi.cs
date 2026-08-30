// WeCodexBgUi.cs -- a modern dark WPF front-end for we-codex-bg.exe.
//
// The helper (we-codex-bg.exe, built from WeCodexBg.cs) is a console tool driven
// entirely by command-line flags.  This UI is a thin, friendly launcher on top of
// it: pick a wallpaper, pick a mode, drag the opacity sliders, hit Start.  It
// spawns the helper as a hidden child process, streams its output into a live log,
// and stops it *gracefully* (so the Codex window is always restored) by posting
// WM_CLOSE to the helper's message-only window, with a --restore fallback.
//
// No XAML: the whole interface is built in code so it compiles with the csc.exe
// that ships with .NET Framework 4.x (same zero-install toolchain as the helper).
//
// Build:  build-ui.bat        ->  bin\we-codex-bg-ui.exe

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using IOPath = System.IO.Path;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        app.Run(new MainWindow());
    }
}

internal sealed class MainWindow : Window
{
    // ---------------------------------------------------------------- palette --

    static readonly Brush Bg       = B("#0F1116");   // window background
    static readonly Brush Panel    = B("#171A22");   // cards / panels
    static readonly Brush Panel2   = B("#1D212B");   // inputs
    static readonly Brush Stroke   = B("#2A303C");   // borders
    static readonly Brush StrokeHi = B("#3A4354");   // hovered borders
    static readonly Brush Text     = B("#E7EAF0");   // primary text
    static readonly Brush Muted    = B("#8B93A7");   // secondary text
    static readonly Brush Faint    = B("#5C637A");   // tertiary text
    static readonly Color AccentC  = C("#5B8CFF");   // brand accent
    static readonly Brush Accent   = new SolidColorBrush(C("#5B8CFF"));
    static readonly Brush AccentHi = new SolidColorBrush(C("#6E9BFF"));
    static readonly Brush GreenC   = B("#3FB950");
    static readonly Brush RedC     = B("#F76D6D");
    static readonly Brush YellowC  = B("#E3B341");

    static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");

    // ---------------------------------------------------------------- controls --

    TextBox _wallpaper, _we, _weWindow, _contentClass, _round, _fps;
    TextBox _wpSearch;
    ListBox _wpList;
    TextBlock _wpCount;
    StackPanel _propHost;              // dynamic wallpaper-property controls
    TextBlock _propHint;
    Slider _volume;
    TextBlock _volumeVal;
    Slider  _alpha, _film, _wallAlpha;
    TextBlock _alphaVal, _filmVal, _wallAlphaVal, _statusText, _cmdPreview;
    Border  _alphaRow, _filmRow, _wallAlphaRow, _embedNote;
    CheckBox _full, _keepWe, _noFallback;
    StackPanel _targetHost;
    TextBlock _targetSummary;
    Grid _targetControls;
    Button  _startBtn, _stopBtn, _restoreBtn;
    RichTextBox _log;
    Paragraph _logPara;
    Ellipse _statusDot;
    System.Windows.Forms.NotifyIcon _tray;
    System.Drawing.Icon _appIcon;
    readonly List<Border> _modeCards = new List<Border>();

    // ------------------------------------------------------------------ state --

    string _mode = "alpha";       // safe default: never touches the content child
    readonly List<WallpaperItem> _library = new List<WallpaperItem>();
    readonly List<TargetChoice> _targetChoices = new List<TargetChoice>();
    readonly List<HelperSession> _sessions = new List<HelperSession>();
    bool _suppressListSelect;      // set while the list is rebuilt programmatically
    bool _targetsInitialized;
    volatile bool _running;
    volatile bool _stopping;
    bool _exitRequested;

    readonly string _appDir = AppDomain.CurrentDomain.BaseDirectory;
    string HelperPath { get { return IOPath.Combine(_appDir, "we-codex-bg.exe"); } }
    static readonly string CfgPath = IOPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "we-codex-bg", "ui.cfg");

    // -------------------------------------------------------------- ctor / UI --

    public MainWindow()
    {
        Title = "Wallpaper Engine · Codex 背景";
        Width = 940; Height = 640; MinWidth = 860; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Foreground = Text;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
        FontSize = 13;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        ApplyAppIcon();

        var chrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, chrome);

        var root = new Border { BorderBrush = Stroke, BorderThickness = new Thickness(1), Background = Bg };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title bar
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(BuildTitleBar());
        grid.Children.Add(BuildBody());
        root.Child = grid;
        Content = root;
        CreateTrayIcon();

        LoadSettings();
        UpdateModeVisuals();
        UpdateCommandPreview();
        SetRunningUi(false);
        RefreshTargets();
        ScanLibraryAsync();

        Closing += OnClosing;
    }

    // --------------------------------------------------------------- title bar --

    UIElement BuildTitleBar()
    {
        var bar = new Border { Height = 46, Background = B("#12141B"), BorderBrush = Stroke,
                               BorderThickness = new Thickness(0, 0, 0, 1) };
        WindowChrome.SetIsHitTestVisibleInChrome(bar, true);
        bar.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var dock = new DockPanel { LastChildFill = true };

        // window buttons on the right
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(btns, Dock.Right);
        btns.Children.Add(CaptionButton("\uE921", false, () => WindowState = WindowState.Minimized));
        btns.Children.Add(CaptionButton("\uE8BB", true, HideToTray));
        dock.Children.Add(btns);

        // brand on the left
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0),
                                     VerticalAlignment = VerticalAlignment.Center };
        var dotWrap = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
                                   Background = Accent, Margin = new Thickness(0, 0, 10, 0) };
        dotWrap.Child = new TextBlock { Text = "\u25D0", FontSize = 13, Foreground = Brushes.White,
                                        HorizontalAlignment = HorizontalAlignment.Center,
                                        VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(dotWrap);
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock { Text = "WE · Codex 背景", FontSize = 13.5,
                                                FontWeight = FontWeights.SemiBold, Foreground = Text });
        titleStack.Children.Add(new TextBlock { Text = "让动态壁纸显示在 Codex / ChatGPT 窗口背后",
                                                FontSize = 10.5, Foreground = Faint });
        brand.Children.Add(titleStack);
        dock.Children.Add(brand);

        bar.Child = dock;
        Grid.SetRow(bar, 0);
        return bar;
    }

    Button CaptionButton(string glyph, bool danger, Action onClick)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 46, Height = 46,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = Muted,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Arrow
        };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Template = FlatTemplate(Brushes.Transparent,
                                  danger ? B("#E11D48") : B("#242A36"),
                                  danger ? Brushes.White : Text, new CornerRadius(0));
        b.Click += (s, e) => onClick();
        return b;
    }

    // -------------------------------------------------------------------- body --

    UIElement BuildBody()
    {
        var g = new Grid { Margin = new Thickness(0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(452) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // -- left: settings (scrollable) --
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 16, 12, 16)
        };
        var col = new StackPanel();
        col.Children.Add(BuildWallpaperSection());
        col.Children.Add(BuildControlSection());
        col.Children.Add(BuildModeSection());
        col.Children.Add(BuildOpacitySection());
        col.Children.Add(BuildTargetSection());
        col.Children.Add(BuildAdvancedSection());
        scroll.Content = col;
        Grid.SetColumn(scroll, 0);
        g.Children.Add(scroll);

        // -- right: log + actions --
        var right = BuildRightPane();
        Grid.SetColumn(right, 1);
        g.Children.Add(right);

        Grid.SetRow(g, 1);
        return g;
    }

    // wallpaper picker ---------------------------------------------------------

    UIElement BuildWallpaperSection()
    {
        var card = Card();
        var sp = (StackPanel)card.Child;
        sp.Children.Add(SectionHeader("壁纸", "自动扫描创意工坊已安装的壁纸，按标题搜索"));

        // -- search box + refresh --
        var searchRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _wpSearch = Input("");
        _wpSearch.TextChanged += (s, e) => RenderLibrary(_wpSearch.Text);
        var searchHost = new Grid();
        searchHost.Children.Add(_wpSearch);
        var placeholder = new TextBlock
        {
            Text = "搜索标题 / 创意工坊 ID / 标签…", Foreground = Faint, FontSize = 12.5,
            Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        _wpSearch.TextChanged += (s, e) =>
            placeholder.Visibility = _wpSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchHost.Children.Add(placeholder);
        Grid.SetColumn(searchHost, 0);
        searchRow.Children.Add(searchHost);

        var rescan = SecondaryButton("重新扫描");
        rescan.Margin = new Thickness(8, 0, 0, 0);
        rescan.Width = 92;
        rescan.Click += (s, e) => ScanLibraryAsync();
        Grid.SetColumn(rescan, 1);
        searchRow.Children.Add(rescan);
        sp.Children.Add(searchRow);

        // -- result list --
        _wpList = new ListBox
        {
            Margin = new Thickness(0, 8, 0, 0), MaxHeight = 260,
            Background = B("#12151C"), BorderBrush = Stroke, BorderThickness = new Thickness(1),
            Foreground = Text, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_wpList, ScrollBarVisibility.Disabled);
        _wpList.ItemContainerStyle = LibraryItemStyle();
        _wpList.SelectionChanged += (s, e) =>
        {
            if (_suppressListSelect) return;
            var it = _wpList.SelectedItem as ListBoxItem;
            var w = it != null ? it.Tag as WallpaperItem : null;
            if (w != null) { _wallpaper.Text = w.ProjectPath; UpdateCommandPreview(); }
        };
        sp.Children.Add(_wpList);

        _wpCount = new TextBlock { Text = "正在扫描…", Foreground = Faint, FontSize = 11,
                                   Margin = new Thickness(2, 6, 0, 0) };
        sp.Children.Add(_wpCount);

        // -- manual path --
        sp.Children.Add(new TextBlock { Text = "壁纸路径", Foreground = Text, FontWeight = FontWeights.Medium,
                                        FontSize = 12.5, Margin = new Thickness(0, 12, 0, 0) });
        sp.Children.Add(new TextBlock { Text = "project.json / .mp4 / .gif —— 留空则接管已打开的壁纸窗口",
                                        Foreground = Faint, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 5) });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _wallpaper = Input("");
        _wallpaper.AllowDrop = true;
        _wallpaper.PreviewDragOver += (s, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        _wallpaper.Drop += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var f = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (f.Length > 0) { _wallpaper.Text = f[0]; UpdateCommandPreview(); }
            }
        };
        _wallpaper.TextChanged += (s, e) =>
        {
            UpdateCommandPreview();
            if (_propHost != null) LoadProperties(_wallpaper.Text.Trim());
        };
        Grid.SetColumn(_wallpaper, 0);
        row.Children.Add(_wallpaper);

        var browse = SecondaryButton("浏览");
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Width = 92;
        browse.Click += (s, e) => BrowseWallpaper();
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);

        sp.Children.Add(row);
        return card;
    }

    void BrowseWallpaper()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Wallpaper Engine 壁纸",
            Filter = "Wallpaper Engine 壁纸 (project.json)|project.json|" +
                     "视频 / 图片 (*.mp4;*.webm;*.gif)|*.mp4;*.webm;*.gif|所有文件 (*.*)|*.*"
        };
        try
        {
            var wsDir = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\431960";
            if (Directory.Exists(wsDir)) dlg.InitialDirectory = wsDir;
        }
        catch { }
        if (dlg.ShowDialog(this) == true) { _wallpaper.Text = dlg.FileName; UpdateCommandPreview(); }
    }

    // wallpaper library: discover ---------------------------------------------

    sealed class WallpaperItem
    {
        public string Title = "", ProjectPath = "", Type = "", WorkshopId = "", Tags = "";
        public ImageSource Thumb;
        public string Haystack = "";      // lower-cased title + id + tags, for matching
    }

    // Scan on a worker thread: it touches disk and decodes preview images.
    void ScanLibraryAsync()
    {
        _wpCount.Text = "正在扫描…";
        var t = new Thread(() =>
        {
            List<WallpaperItem> found;
            try { found = ScanLibrary(); }
            catch (Exception ex)
            {
                Dispatch(() => { _wpCount.Text = "扫描失败：" + ex.Message; });
                return;
            }
            Dispatch(() =>
            {
                _library.Clear();
                _library.AddRange(found);
                RenderLibrary(_wpSearch.Text);
            });
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);   // BitmapImage wants STA
        t.Start();
    }

    static List<WallpaperItem> ScanLibrary()
    {
        var list = new List<WallpaperItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in WallpaperFolders())
        {
            string pj = IOPath.Combine(dir, "project.json");
            if (!File.Exists(pj) || !seen.Add(pj)) continue;

            Dictionary<string, string> j;
            try { j = JsonTopLevelStrings(File.ReadAllText(pj, Encoding.UTF8)); }
            catch { continue; }

            var w = new WallpaperItem { ProjectPath = pj };
            string v;
            w.Title = j.TryGetValue("title", out v) && v.Trim().Length > 0
                      ? v.Trim() : new DirectoryInfo(dir).Name;
            w.Type = j.TryGetValue("type", out v) ? v.ToLowerInvariant() : "";
            w.WorkshopId = j.TryGetValue("workshopid", out v) ? v : new DirectoryInfo(dir).Name;
            w.Tags = j.TryGetValue("tags", out v) ? v : "";

            if (j.TryGetValue("preview", out v) && v.Length > 0)
            {
                string img;
                if (TryResolvePreviewPath(dir, v, out img)) w.Thumb = LoadThumb(img);
            }
            w.Haystack = (w.Title + " " + w.WorkshopId + " " + w.Tags).ToLowerInvariant();
            list.Add(w);
        }
        list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    // Every directory that may hold a project.json: workshop content for app
    // 431960 in each Steam library, plus Wallpaper Engine's own local projects.
    static IEnumerable<string> WallpaperFolders()
    {
        foreach (string root in SteamLibraries())
        {
            string ws = IOPath.Combine(root, @"steamapps\workshop\content\431960");
            foreach (string d in SafeDirs(ws)) yield return d;

            string my = IOPath.Combine(root, @"steamapps\common\wallpaper_engine\projects\myprojects");
            foreach (string d in SafeDirs(my)) yield return d;

            string dflt = IOPath.Combine(root, @"steamapps\common\wallpaper_engine\projects\defaultprojects");
            foreach (string d in SafeDirs(dflt)) yield return d;
        }
    }

    static string[] SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : new string[0]; }
        catch { return new string[0]; }
    }

    // Steam roots: registry first (the install is often NOT under Program Files),
    // then every library listed in libraryfolders.vdf, then the usual defaults.
    static List<string> SteamLibraries()
    {
        var roots = new List<string>();
        Action<string> add = p =>
        {
            if (string.IsNullOrEmpty(p)) return;
            p = p.Replace('/', '\\').TrimEnd('\\');
            if (p.Length == 0) return;
            foreach (var r in roots) if (string.Equals(r, p, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(p);
        };

        add(RegString(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"));
        add(RegString(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        add(RegString(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"));
        add(@"C:\Program Files (x86)\Steam");
        add(@"C:\Program Files\Steam");

        // libraryfolders.vdf can point at libraries on other drives
        for (int i = 0; i < roots.Count; i++)
        {
            foreach (string vdf in new[] { @"steamapps\libraryfolders.vdf", @"config\libraryfolders.vdf" })
            {
                string p = IOPath.Combine(roots[i], vdf);
                try
                {
                    if (!File.Exists(p)) continue;
                    foreach (string lib in VdfPaths(File.ReadAllText(p))) add(lib);
                }
                catch { }
            }
        }
        return roots;
    }

    static string RegString(string key, string name)
    {
        try { return Microsoft.Win32.Registry.GetValue(key, name, null) as string; }
        catch { return null; }
    }

    // Pull every  "path"  "X:\\some\\dir"  pair out of a libraryfolders.vdf.
    static List<string> VdfPaths(string vdf)
    {
        var outp = new List<string>();
        int i = 0;
        while (true)
        {
            int k = vdf.IndexOf("\"path\"", i, StringComparison.OrdinalIgnoreCase);
            if (k < 0) break;
            i = k + 6;
            int q1 = vdf.IndexOf('"', i);
            if (q1 < 0) break;
            int q2 = vdf.IndexOf('"', q1 + 1);
            if (q2 < 0) break;
            outp.Add(vdf.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\"));
            i = q2 + 1;
        }
        return outp;
    }

    // project.json is supplied by Workshop content and must not be allowed to
    // turn an automatic library scan into a request to a UNC/WebDAV endpoint.
    // Accept only ordinary relative image names whose canonical path stays in
    // the wallpaper directory.
    static bool TryResolvePreviewPath(string wallpaperDir, string preview, out string file)
    {
        file = null;
        if (string.IsNullOrWhiteSpace(preview) || IOPath.IsPathRooted(preview)) return false;

        try
        {
            string root = IOPath.GetFullPath(wallpaperDir)
                .TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar)
                + IOPath.DirectorySeparatorChar;
            string candidate = IOPath.GetFullPath(IOPath.Combine(root, preview));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            string ext = IOPath.GetExtension(candidate).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" &&
                ext != ".bmp" && ext != ".gif") return false;

            file = candidate;
            return true;
        }
        catch { return false; }
    }

    static ImageSource LoadThumb(string file)
    {
        try
        {
            const long MaxPreviewBytes = 20L * 1024 * 1024;
            var info = new FileInfo(file);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxPreviewBytes) return null;

            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              64 * 1024, FileOptions.SequentialScan))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = input;
                bi.DecodePixelWidth = 96;             // keep memory small
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.EndInit();
                bi.Freeze();                          // required to cross threads
                return bi;
            }
        }
        catch { return null; }
    }

    // -- minimal JSON reader: top-level string values only.  project.json is
    // machine-written by Wallpaper Engine, but descriptions contain quotes,
    // newlines and \u escapes, so the scanner tracks depth and escapes properly.
    static Dictionary<string, string> JsonTopLevelStrings(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0, depth = 0;
        string key = null;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '{' || c == '[') { depth++; i++; key = null; continue; }
            if (c == '}' || c == ']') { depth--; i++; key = null; continue; }
            if (c == '"')
            {
                string val; int end;
                if (!ReadJsonString(s, i, out val, out end)) break;
                i = end;
                int j = i;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j < s.Length && s[j] == ':')          // it was a key
                {
                    if (depth == 1) key = val;
                    i = j + 1;
                }
                else                                      // it was a value
                {
                    if (depth == 1 && key != null) d[key] = val;
                    else if (depth == 2 && key != null)   // string inside a top-level array
                        d[key] = d.ContainsKey(key) && d[key].Length > 0 ? d[key] + ", " + val : val;
                    if (depth == 1) key = null;
                }
                continue;
            }
            if (c == ',' && depth == 1) key = null;
            i++;
        }
        return d;
    }

    static bool ReadJsonString(string s, int start, out string val, out int end)
    {
        val = null; end = start;
        if (start >= s.Length || s[start] != '"') return false;
        var sb = new StringBuilder();
        int i = start + 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n == 'u' && i + 5 < s.Length)
                {
                    int code;
                    if (int.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber,
                                     CultureInfo.InvariantCulture, out code))
                    { sb.Append((char)code); i += 6; continue; }
                }
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    default: sb.Append(n); break;
                }
                i += 2;
                continue;
            }
            if (c == '"') { val = sb.ToString(); end = i + 1; return true; }
            sb.Append(c);
            i++;
        }
        return false;
    }

    // wallpaper library: render ------------------------------------------------

    void RenderLibrary(string filter)
    {
        if (_wpList == null) return;
        filter = (filter ?? "").Trim().ToLowerInvariant();
        string current = _wallpaper != null ? _wallpaper.Text.Trim() : "";

        _suppressListSelect = true;
        _wpList.Items.Clear();
        int shown = 0;
        foreach (var w in _library)
        {
            if (filter.Length > 0 && w.Haystack.IndexOf(filter, StringComparison.Ordinal) < 0) continue;
            var item = LibraryRow(w);
            _wpList.Items.Add(item);
            if (string.Equals(w.ProjectPath, current, StringComparison.OrdinalIgnoreCase))
                _wpList.SelectedItem = item;
            shown++;
        }
        _suppressListSelect = false;

        if (_library.Count == 0)
            _wpCount.Text = "未找到已安装的壁纸 —— 可在下方手动填写路径";
        else if (filter.Length > 0)
            _wpCount.Text = string.Format("匹配 {0} / 共 {1} 张", shown, _library.Count);
        else
            _wpCount.Text = string.Format("共找到 {0} 张壁纸", _library.Count);
    }

    ListBoxItem LibraryRow(WallpaperItem w)
    {
        var g = new Grid { Margin = new Thickness(0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var thumbBox = new Border
        {
            Width = 48, Height = 30, CornerRadius = new CornerRadius(4),
            Background = B("#20252F"), Margin = new Thickness(0, 0, 10, 0),
            ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center
        };
        if (w.Thumb != null)
            thumbBox.Child = new Image { Source = w.Thumb, Stretch = Stretch.UniformToFill };
        Grid.SetColumn(thumbBox, 0);
        g.Children.Add(thumbBox);

        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(new TextBlock
        {
            Text = w.Title, Foreground = Text, FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        string kind = w.Type == "scene" ? "场景" : w.Type == "video" ? "视频"
                    : w.Type == "web" ? "网页" : w.Type.Length > 0 ? w.Type : "未知";
        st.Children.Add(new TextBlock
        {
            Text = kind + "  ·  " + w.WorkshopId, Foreground = Faint, FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(st, 1);
        g.Children.Add(st);

        return new ListBoxItem { Content = g, Tag = w, Padding = new Thickness(8, 6, 8, 6),
                                 ToolTip = w.Title + "\n" + w.ProjectPath };
    }

    // dark selection/hover states for the library rows
    Style LibraryItemStyle()
    {
        var t = new ControlTemplate(typeof(ListBoxItem));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        bd.AppendChild(cp);
        t.VisualTree = bd;

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, B("#1C2130"), "bd"));
        t.Triggers.Add(over);

        var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Border.BackgroundProperty, B("#1B2436"), "bd"));
        t.Triggers.Add(sel);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, t));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(3, 2, 3, 2)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        return style;
    }

    // control module -----------------------------------------------------------
    //
    // Transport uses Wallpaper Engine's documented CLI.  Its CLI has mute/unmute,
    // but no volume command, so the volume slider targets WE's own mixer sessions.

    UIElement BuildControlSection()
    {
        var card = Card();
        var sp = (StackPanel)card.Child;
        sp.Children.Add(SectionHeader("控制", "播放与壁纸自身的参数，实时生效"));

        // -- transport row --
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(ControlChip("▶  播放", () => WeControl("play")));
        row.Children.Add(ControlChip("⏸  暂停", () => WeControl("pause")));
        row.Children.Add(ControlChip("⏹  停止", () => WeControl("stop")));
        row.Children.Add(ControlChip("\U0001F507  静音", () => WeControl("mute")));
        row.Children.Add(ControlChip("\U0001F50A  取消静音", () => WeControl("unmute")));
        sp.Children.Add(row);

        // -- volume --
        _volume = MakeSlider(0, 100, 100);
        _volumeVal = ValueBadge("100");
        _volume.ValueChanged += (s, e) => _volumeVal.Text = ((int)_volume.Value).ToString();
        _volume.PreviewMouseUp += (s, e) => ApplyWallpaperVolume();
        sp.Children.Add(SliderRow("壁纸音量", "拖动结束后应用（仅 Wallpaper Engine）", _volume, _volumeVal));

        // -- dynamic wallpaper properties --
        _propHint = new TextBlock
        {
            Text = "选择一张壁纸后，这里会列出它自己的可调参数。", Foreground = Faint,
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
        };
        sp.Children.Add(_propHint);
        _propHost = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        sp.Children.Add(_propHost);
        return card;
    }

    Button ControlChip(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Height = 32, Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 0, 10, 0), Foreground = Text,
            FontSize = 11.5, Cursor = Cursors.Hand
        };
        b.Template = FlatTemplate(Panel2, B("#252B37"), Text, new CornerRadius(8), Stroke, 1);
        b.Click += (s, e) => onClick();
        return b;
    }

    // Run `wallpaper64.exe -control <cmd> [args...]` and report failures in the log.
    void WeControl(string cmd, params string[] extra)
    {
        string exe = ResolveWeExe();
        if (exe == null)
        {
            AppendLog("[!] 找不到 wallpaper64.exe，无法发送控制命令（可在高级选项里手动指定）。", RedC);
            return;
        }
        var args = new List<string> { "-control", cmd };
        args.AddRange(extra);
        try
        {
            var psi = new ProcessStartInfo(exe, JoinArgs(args))
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            Process.Start(psi);
            AppendLog("[we] -control " + cmd + (extra.Length > 0 ? " " + string.Join(" ", extra) : ""), Faint);
        }
        catch (Exception ex) { AppendLog("[!] 控制命令失败：" + ex.Message, RedC); }
    }

    void ApplyWallpaperVolume()
    {
        int value = (int)_volume.Value;
        try
        {
            int count = WallpaperAudio.SetVolume(value / 100f);
            if (count > 0)
                AppendLog("[audio] Wallpaper Engine 音量 = " + value + "（" + count + " 个会话）", Faint);
            else
                AppendLog("[!] 未找到正在输出声音的 Wallpaper Engine 会话。", YellowC);
        }
        catch (Exception ex) { AppendLog("[!] 设置壁纸音量失败：" + ex.Message, RedC); }
    }

    string ResolveWeExe()
    {
        string manual = _we != null ? _we.Text.Trim() : "";
        if (manual.Length > 0 && File.Exists(manual)) return manual;
        // a running instance is the most reliable source
        foreach (var p in Process.GetProcesses())
        {
            string n = p.ProcessName.ToLowerInvariant();
            if (n != "wallpaper64" && n != "wallpaper32") continue;
            try
            {
                string full = p.MainModule.FileName;
                string dir = IOPath.GetDirectoryName(full);
                string c = IOPath.Combine(dir, "wallpaper64.exe");
                if (File.Exists(c)) return c;
                return full;
            }
            catch { }
        }
        foreach (string root in SteamLibraries())
        {
            string c = IOPath.Combine(root, @"steamapps\common\wallpaper_engine\wallpaper64.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    // -- properties declared by the wallpaper itself (project.json general.properties) --

    sealed class PropCtl
    {
        public string Name = "", Type = "";
        public Func<string> Read;          // current value, already JSON-ready
    }
    readonly List<PropCtl> _props = new List<PropCtl>();

    void LoadProperties(string projectJsonPath)
    {
        _props.Clear();
        _propHost.Children.Clear();

        if (string.IsNullOrEmpty(projectJsonPath) || !File.Exists(projectJsonPath) ||
            !projectJsonPath.EndsWith("project.json", StringComparison.OrdinalIgnoreCase))
        {
            _propHint.Text = "选择一张壁纸后，这里会列出它自己的可调参数。";
            return;
        }

        JVal root;
        try { root = JVal.Parse(File.ReadAllText(projectJsonPath, Encoding.UTF8)); }
        catch (Exception ex) { _propHint.Text = "读取参数失败：" + ex.Message; return; }

        JVal props = root["general"] != null ? root["general"]["properties"] : null;
        if (props == null || props.Kind != JKind.Object)
        {
            _propHint.Text = "这张壁纸没有暴露可调参数。";
            return;
        }

        foreach (string key in props.Keys)
        {
            JVal p = props[key];
            if (p == null || p.Kind != JKind.Object) continue;
            string type = p["type"] != null ? p["type"].AsString() : "";
            string label = p["text"] != null ? p["text"].AsString() : key;
            label = PrettyLabel(label, key);
            UIElement ctl = BuildPropControl(key, type, label, p);
            if (ctl != null) _propHost.Children.Add(ctl);
        }

        _propHint.Text = _props.Count > 0
            ? string.Format("这张壁纸暴露了 {0} 个可调参数（改动立即应用）：", _props.Count)
            : "这张壁纸没有可直接调节的参数。";
    }

    // WE labels come in two flavours: localisation keys like
    // "ui_browse_properties_scheme_color", and author-written text that may carry
    // simple HTML markup ("<big><b>音频感应大小").  Both need cleaning up.
    static string PrettyLabel(string text, string key)
    {
        if (text.StartsWith("ui_", StringComparison.OrdinalIgnoreCase))
        {
            string s = text;
            foreach (string pre in new[] { "ui_browse_properties_", "ui_editor_properties_", "ui_" })
                if (s.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(pre.Length); break; }
            s = s.Replace('_', ' ').Trim();
            if (s.Length > 0) return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
        string t = StripMarkup(text).Trim();
        return t.Length > 0 ? t : key;
    }

    static string StripMarkup(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
        var sb = new StringBuilder(s.Length);
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '<') { depth++; continue; }
            if (c == '>') { if (depth > 0) depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    UIElement BuildPropControl(string name, string type, string label, JVal p)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var head = new TextBlock { Text = label, Foreground = Text, FontSize = 12,
                                   TextTrimming = TextTrimming.CharacterEllipsis };

        switch (type)
        {
            case "bool":
            {
                var cb = new CheckBox { Content = label, Foreground = Text, FontSize = 12,
                                        IsChecked = p["value"] != null && p["value"].AsBool(),
                                        Cursor = Cursors.Hand, Margin = new Thickness(0, 8, 0, 0) };
                cb.Checked += (s, e) => ApplyProperty(name, "true");
                cb.Unchecked += (s, e) => ApplyProperty(name, "false");
                _props.Add(new PropCtl { Name = name, Type = type,
                                         Read = () => cb.IsChecked == true ? "true" : "false" });
                return cb;
            }
            case "slider":
            {
                double min = p["min"] != null ? p["min"].AsNumber(0) : 0;
                double max = p["max"] != null ? p["max"].AsNumber(1) : 1;
                if (max <= min) max = min + 1;
                double val = p["value"] != null ? p["value"].AsNumber(min) : min;
                var sl = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)),
                                      Foreground = Accent, Height = 24,
                                      IsSnapToTickEnabled = false };
                var badge = ValueBadge(sl.Value.ToString("0.##", CultureInfo.InvariantCulture));
                sl.ValueChanged += (s, e) => badge.Text = sl.Value.ToString("0.##", CultureInfo.InvariantCulture);
                sl.PreviewMouseUp += (s, e) =>
                    ApplyProperty(name, sl.Value.ToString("0.####", CultureInfo.InvariantCulture));
                _props.Add(new PropCtl { Name = name, Type = type,
                                         Read = () => sl.Value.ToString("0.####", CultureInfo.InvariantCulture) });
                return SliderRow(label, "拖动结束后应用", sl, badge);
            }
            case "combo":
            {
                var cbx = new ComboBox { Height = 32, Background = Panel2, Foreground = Text,
                                         BorderBrush = Stroke, BorderThickness = new Thickness(1),
                                         Margin = new Thickness(0, 4, 0, 0),
                                         VerticalContentAlignment = VerticalAlignment.Center };
                var values = new List<string>();
                JVal opts = p["options"];
                if (opts != null && opts.Kind == JKind.Array)
                    foreach (JVal o in opts.Array)
                    {
                        string v = o["value"] != null ? o["value"].AsString() : "";
                        string t = o["label"] != null ? o["label"].AsString() : v;
                        values.Add(v);
                        cbx.Items.Add(t);
                    }
                string cur = p["value"] != null ? p["value"].AsString() : "";
                int idx = values.IndexOf(cur);
                if (idx >= 0) cbx.SelectedIndex = idx;
                cbx.SelectionChanged += (s, e) =>
                {
                    int k = cbx.SelectedIndex;
                    if (k >= 0 && k < values.Count) ApplyProperty(name, values[k]);
                };
                _props.Add(new PropCtl { Name = name, Type = type, Read = () =>
                {
                    int k = cbx.SelectedIndex;
                    return k >= 0 && k < values.Count ? values[k] : "";
                } });
                wrap.Children.Add(head);
                wrap.Children.Add(cbx);
                return wrap;
            }
            case "color":
            {
                // WE colours are "r g b" floats in 0..1
                string cur = p["value"] != null ? p["value"].AsString("1 1 1") : "1 1 1";
                var tb = Input(cur);
                tb.Height = 32;
                var sw = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6),
                                      BorderBrush = Stroke, BorderThickness = new Thickness(1),
                                      Margin = new Thickness(8, 0, 0, 0),
                                      Background = ColorFromWe(cur) };
                tb.TextChanged += (s, e) => sw.Background = ColorFromWe(tb.Text);
                tb.LostKeyboardFocus += (s, e) => ApplyProperty(name, tb.Text.Trim());
                var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(tb, 0); g.Children.Add(tb);
                Grid.SetColumn(sw, 1); g.Children.Add(sw);
                _props.Add(new PropCtl { Name = name, Type = type, Read = () => tb.Text.Trim() });
                wrap.Children.Add(head);
                wrap.Children.Add(new TextBlock { Text = "RGB 0-1，例如 0.2 0.4 1", Foreground = Faint, FontSize = 10 });
                wrap.Children.Add(g);
                return wrap;
            }
            case "textinput":
            {
                var tb = Input(p["value"] != null ? p["value"].AsString() : "");
                tb.Height = 32;
                tb.Margin = new Thickness(0, 4, 0, 0);
                tb.LostKeyboardFocus += (s, e) => ApplyProperty(name, tb.Text);
                _props.Add(new PropCtl { Name = name, Type = type, Read = () => tb.Text });
                wrap.Children.Add(head);
                wrap.Children.Add(tb);
                return wrap;
            }
            case "group":
                // WE renders these as section dividers in its own property panel
                return new TextBlock
                {
                    Text = label, Foreground = Accent, FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2)
                };

            case "text":
                return new TextBlock
                {
                    Text = label, Foreground = Muted, FontSize = 11,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
                };

            default:
                return null;             // file / scenetexture: nothing sensible to offer here
        }
    }

    static Brush ColorFromWe(string s)
    {
        try
        {
            var parts = (s ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return Panel2;
            double r = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double g = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double b = double.Parse(parts[2], CultureInfo.InvariantCulture);
            Func<double, byte> f = v => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
            return new SolidColorBrush(Color.FromRgb(f(r), f(g), f(b)));
        }
        catch { return Panel2; }
    }

    // wallpaper64.exe -control applyProperties -properties RAW~({"name":{"value":X}})~
    void ApplyProperty(string name, string value)
    {
        bool numeric;
        double d;
        numeric = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        string json;
        if (value == "true" || value == "false")
            json = "{\"" + JVal.Escape(name) + "\":{\"value\":" + value + "}}";
        else if (numeric)
            json = "{\"" + JVal.Escape(name) + "\":{\"value\":" + value + "}}";
        else
            json = "{\"" + JVal.Escape(name) + "\":{\"value\":\"" + JVal.Escape(value) + "\"}}";

        WeControl("applyProperties", "-properties", "RAW~(" + json + ")~");
    }

    // mode selector ------------------------------------------------------------

    UIElement BuildModeSection()
    {
        var card = Card();
        var sp = (StackPanel)card.Child;
        sp.Children.Add(SectionHeader("模式", "动画与 Codex 窗口的合成方式"));

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
        grid.Children.Add(ModeCard("alpha", "透明 · Alpha", "钉在窗口正下方，整个窗口半透明。推荐默认，Codex 保持完全可操作。"));
        grid.Children.Add(ModeCard("overlay", "覆盖 · Overlay", "作为可穿透的半透明膜盖在界面上，完全不修改 Codex。最安全。"));
        grid.Children.Add(ModeCard("composite", "合成 · Composite ⚠", "只淡化页面内容、边框保持清晰，但对 Codex/ChatGPT 这类 Electron 宿主会导致界面完全失去鼠标响应，已自动拦截。"));
        grid.Children.Add(ModeCard("embed", "嵌入 · Embed", "只嵌入、不做透明处理。管线测试 / 未来支持透明背景的宿主。"));
        sp.Children.Add(grid);
        return card;
    }

    Border ModeCard(string id, string title, string desc)
    {
        var card = new Border
        {
            Margin = new Thickness(3),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            Background = Panel2,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = id
        };
        var st = new StackPanel();
        st.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5,
                                        Foreground = Text, Margin = new Thickness(0, 0, 0, 3) });
        st.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = Muted,
                                        TextWrapping = TextWrapping.Wrap, LineHeight = 15 });
        card.Child = st;
        card.MouseLeftButtonUp += (s, e) => { _mode = id; UpdateModeVisuals(); UpdateCommandPreview(); };
        card.MouseEnter += (s, e) => { if (_mode != id) card.BorderBrush = StrokeHi; };
        card.MouseLeave += (s, e) => { if (_mode != id) card.BorderBrush = Stroke; };
        _modeCards.Add(card);
        return card;
    }

    void UpdateModeVisuals()
    {
        foreach (var c in _modeCards)
        {
            bool sel = (string)c.Tag == _mode;
            c.BorderBrush = sel ? Accent : Stroke;
            c.BorderThickness = new Thickness(sel ? 1.6 : 1);
            c.Background = sel ? B("#1B2436") : Panel2;
        }
        bool showAlpha = _mode == "composite" || _mode == "alpha";
        bool showFilm = _mode == "overlay";
        _alphaRow.Visibility = showAlpha ? Visibility.Visible : Visibility.Collapsed;
        _filmRow.Visibility = showFilm ? Visibility.Visible : Visibility.Collapsed;
        // the wallpaper is layered in every non-overlay mode, so it can always be dimmed
        _wallAlphaRow.Visibility = showFilm ? Visibility.Collapsed : Visibility.Visible;
        _embedNote.Visibility = _mode == "embed" ? Visibility.Visible : Visibility.Collapsed;
    }

    // opacity ------------------------------------------------------------------

    UIElement BuildOpacitySection()
    {
        var card = Card();
        var sp = (StackPanel)card.Child;
        sp.Children.Add(SectionHeader("不透明度", "运行中拖动即时生效，不用重启"));

        _alpha = MakeSlider(0, 255, 235);
        _alphaVal = ValueBadge("235");
        _alphaRow = SliderRow("宿主不透明度", "越高 = 文字越清晰；壁纸太亮就调高这个", _alpha, _alphaVal);
        _alpha.ValueChanged += (s, e) =>
        {
            _alphaVal.Text = ((int)_alpha.Value).ToString();
            UpdateCommandPreview();
            SendLiveAlpha(WM_SET_HOST_ALPHA, (int)_alpha.Value);
        };
        sp.Children.Add(_alphaRow);

        _wallAlpha = MakeSlider(0, 255, 255);
        _wallAlphaVal = ValueBadge("255");
        _wallAlphaRow = SliderRow("壁纸亮度", "越低 = 壁纸越暗；比淡化界面更能保住文字对比度", _wallAlpha, _wallAlphaVal);
        _wallAlpha.ValueChanged += (s, e) =>
        {
            _wallAlphaVal.Text = ((int)_wallAlpha.Value).ToString();
            UpdateCommandPreview();
            SendLiveAlpha(WM_SET_WALL_ALPHA, (int)_wallAlpha.Value);
        };
        sp.Children.Add(_wallAlphaRow);

        _film = MakeSlider(0, 255, 70);
        _filmVal = ValueBadge("70");
        _filmRow = SliderRow("壁纸膜不透明度", "盖在界面上的壁纸的不透明度", _film, _filmVal);
        _film.ValueChanged += (s, e) =>
        {
            _filmVal.Text = ((int)_film.Value).ToString();
            UpdateCommandPreview();
            SendLiveAlpha(WM_SET_WALL_ALPHA, (int)_film.Value);
        };
        sp.Children.Add(_filmRow);

        var tip = new Border
        {
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8), Background = B("#1A1E27"),
            BorderBrush = Stroke, BorderThickness = new Thickness(1)
        };
        tip.Child = new TextBlock
        {
            Text = "界面被壁纸冲得发白？先把「壁纸亮度」往下拉（比如 120），再把「宿主不透明度」拉到 240 以上。",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap
        };
        sp.Children.Add(tip);

        _embedNote = new Border
        {
            Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8), Background = B("#1A1E27"),
            BorderBrush = Stroke, BorderThickness = new Thickness(1)
        };
        _embedNote.Child = new TextBlock
        {
            Text = "纯嵌入模式不做任何透明处理 —— 只有当宿主自身绘制透明背景时才能看到壁纸。",
            Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap
        };
        sp.Children.Add(_embedNote);
        return card;
    }

    Border SliderRow(string label, string hint, Slider slider, TextBlock badge)
    {
        var wrap = new Border { Margin = new Thickness(0, 10, 0, 0) };
        var st = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lt = new StackPanel();
        lt.Children.Add(new TextBlock { Text = label, Foreground = Text, FontWeight = FontWeights.Medium, FontSize = 12.5 });
        lt.Children.Add(new TextBlock { Text = hint, Foreground = Faint, FontSize = 10.5 });
        Grid.SetColumn(lt, 0); head.Children.Add(lt);
        Grid.SetColumn(badge, 1); head.Children.Add(badge);
        st.Children.Add(head);

        slider.Margin = new Thickness(0, 4, 0, 0);
        st.Children.Add(slider);
        wrap.Child = st;
        return wrap;
    }

    // target -------------------------------------------------------------------

    UIElement BuildTargetSection()
    {
        var card = Card();
        var sp = (StackPanel)card.Child;
        sp.Children.Add(SectionHeader("目标窗口", "可同时选择多个窗口；默认包含所有 Codex / ChatGPT 窗口"));

        _targetControls = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        _targetControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _targetControls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(158) });
        _targetControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(new TextBlock
        {
            Text = "勾选后会为每个窗口启动独立壁纸实例",
            Foreground = Faint, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var clear = SecondaryButton("清空");
        clear.Width = 62; clear.Height = 30;
        clear.Click += (s, e) =>
        {
            foreach (var choice in _targetChoices) choice.Check.IsChecked = false;
        };
        var refresh = SecondaryButton("刷新");
        refresh.Width = 62; refresh.Height = 30; refresh.Margin = new Thickness(6, 0, 0, 0);
        refresh.Click += (s, e) => RefreshTargets();
        buttons.Children.Add(clear);
        buttons.Children.Add(refresh);
        Grid.SetColumn(buttons, 1);
        toolbar.Children.Add(buttons);
        Grid.SetRow(toolbar, 0);
        _targetControls.Children.Add(toolbar);

        _targetHost = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };
        var scroll = new ScrollViewer
        {
            Content = _targetHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var listBorder = new Border
        {
            Background = Panel2, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = scroll
        };
        Grid.SetRow(listBorder, 1);
        _targetControls.Children.Add(listBorder);

        _targetSummary = new TextBlock
        {
            Foreground = Muted, FontSize = 10.5, Margin = new Thickness(2, 7, 2, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_targetSummary, 2);
        _targetControls.Children.Add(_targetSummary);
        sp.Children.Add(_targetControls);
        return card;
    }

    sealed class WinItem
    {
        public IntPtr Hwnd { get; set; }
        public string Display { get; set; }
        public string Title { get; set; }
        public string Exe { get; set; }
        public uint Pid { get; set; }
        public bool IsCodex { get; set; }
        public bool IsAuto { get; set; }
        public override string ToString() { return Display; }
    }

    sealed class TargetChoice
    {
        public WinItem Item { get; set; }
        public CheckBox Check { get; set; }
    }

    sealed class HelperSession
    {
        public WinItem Target { get; set; }
        public Process Process { get; set; }
        public string WeWindow { get; set; }
        public bool StopRequested { get; set; }
        public bool StopWaitFinished { get; set; }
    }

    CheckBox TargetCheck(WinItem item, bool selected)
    {
        var cb = new CheckBox
        {
            IsChecked = selected, Tag = item, Foreground = Text, Cursor = Cursors.Hand,
            Margin = new Thickness(6, 4, 6, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = item.IsAuto ? "自动包含所有 Codex / ChatGPT 窗口" : item.Title,
            Foreground = Text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = item.IsAuto ? "启动时按当前可见窗口展开；适合同时运行多个 agent"
                               : item.Exe + "  ·  PID " + item.Pid + "  ·  HWND 0x" + item.Hwnd.ToInt64().ToString("X"),
            Foreground = Faint, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis
        });
        cb.Content = text;
        cb.Checked += TargetSelectionChanged;
        cb.Unchecked += TargetSelectionChanged;
        return cb;
    }

    void TargetSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateTargetSummary();
        UpdateCommandPreview();
    }

    static bool IsCodexWindow(string exe, string title)
    {
        exe = (exe ?? "").ToLowerInvariant();
        title = (title ?? "").ToLowerInvariant();
        return exe == "codex.exe" || exe == "chatgpt.exe" || exe == "openai.exe" ||
               exe == "chatgpt-desktop.exe" || title.Contains("codex") || title.Contains("chatgpt");
    }

    List<WinItem> SelectedTargets()
    {
        var result = new List<WinItem>();
        var seen = new HashSet<long>();
        bool automatic = false;
        int codexCount = 0;
        foreach (var choice in _targetChoices)
            if (choice.Item.IsAuto && choice.Check.IsChecked == true) automatic = true;

        if (automatic)
        {
            foreach (var choice in _targetChoices)
            {
                var item = choice.Item;
                if (item.IsAuto || !item.IsCodex || item.Hwnd == IntPtr.Zero) continue;
                if (seen.Add(item.Hwnd.ToInt64())) result.Add(item);
                codexCount++;
            }
        }
        foreach (var choice in _targetChoices)
        {
            var item = choice.Item;
            if (item.IsAuto || choice.Check.IsChecked != true || item.Hwnd == IntPtr.Zero) continue;
            if (seen.Add(item.Hwnd.ToInt64())) result.Add(item);
        }

        if (automatic && codexCount == 0 && result.Count == 0)
            result.Add(new WinItem { IsAuto = true, Display = "自动检测 Codex / ChatGPT", Title = "Codex 自动检测" });
        return result;
    }

    void UpdateTargetSummary()
    {
        if (_targetSummary == null) return;
        var targets = SelectedTargets();
        if (targets.Count == 0) _targetSummary.Text = "尚未选择目标窗口。";
        else if (targets.Count == 1 && targets[0].IsAuto) _targetSummary.Text = "未发现可见 Codex，启动时仍会自动检测。";
        else _targetSummary.Text = "将同时挂载到 " + targets.Count + " 个窗口。";
    }

    void RefreshTargets()
    {
        bool automatic = !_targetsInitialized;
        var selected = new HashSet<long>();
        foreach (var choice in _targetChoices)
        {
            if (choice.Item.IsAuto) automatic = choice.Check.IsChecked == true;
            else if (choice.Check.IsChecked == true) selected.Add(choice.Item.Hwnd.ToInt64());
        }

        var items = new List<WinItem>();
        uint self = (uint)Process.GetCurrentProcess().Id;
        Native.EnumWindows((h, l) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            Native.RECT rc;
            if (!Native.GetWindowRect(h, out rc)) return true;
            if (rc.R - rc.L < 200 || rc.B - rc.T < 120) return true;
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
            var title = Native.Text(h);
            if (title.Length == 0) return true;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pid == self) return true;
            string exe = Native.ExeName(pid);
            if (exe == "wallpaper64.exe" || exe == "wallpaper32.exe") return true;
            if (title == "Program Manager") return true;
            string disp = title;
            if (disp.Length > 46) disp = disp.Substring(0, 45) + "\u2026";
            items.Add(new WinItem
            {
                Hwnd = h, Pid = pid, Title = title, Exe = exe,
                Display = disp + "  ·  " + exe + "  ·  PID " + pid,
                IsCodex = IsCodexWindow(exe, title)
            });
            return true;
        }, IntPtr.Zero);

        items.Sort((a, b) =>
        {
            if (a.IsCodex != b.IsCodex) return a.IsCodex ? -1 : 1;
            return string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase);
        });

        _targetHost.Children.Clear();
        _targetChoices.Clear();
        var autoItem = new WinItem
        {
            IsAuto = true, IsCodex = true, Title = "自动包含所有 Codex / ChatGPT 窗口",
            Display = "自动包含所有 Codex / ChatGPT 窗口"
        };
        var autoCheck = TargetCheck(autoItem, automatic);
        _targetChoices.Add(new TargetChoice { Item = autoItem, Check = autoCheck });
        _targetHost.Children.Add(autoCheck);

        foreach (var item in items)
        {
            var check = TargetCheck(item, selected.Contains(item.Hwnd.ToInt64()));
            _targetChoices.Add(new TargetChoice { Item = item, Check = check });
            _targetHost.Children.Add(check);
        }
        _targetsInitialized = true;
        UpdateTargetSummary();
        UpdateCommandPreview();
    }

    // advanced -----------------------------------------------------------------

    UIElement BuildAdvancedSection()
    {
        var exp = new Expander
        {
            Header = "高级选项",
            Foreground = Muted,
            Margin = new Thickness(0, 2, 0, 4),
            FontSize = 12.5,
            IsExpanded = false
        };

        var card = new Border
        {
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14),
            CornerRadius = new CornerRadius(12), Background = Panel,
            BorderBrush = Stroke, BorderThickness = new Thickness(1)
        };
        var sp = new StackPanel();

        _we = Input("");
        sp.Children.Add(LabeledInput("Wallpaper Engine 路径", "wallpaper64.exe —— 留空则自动检测", _we, true));

        _weWindow = Input("CodexWallpaperHost");
        _weWindow.TextChanged += (s, e) => UpdateCommandPreview();
        sp.Children.Add(LabeledInput("宿主窗口名称前缀", "多窗口时会自动追加目标 HWND", _weWindow, false));

        _contentClass = Input("");
        _contentClass.TextChanged += (s, e) => UpdateCommandPreview();
        sp.Children.Add(LabeledInput("内容窗口类名 (合成模式)", "例如 Chrome_RenderWidgetHostHWND", _contentClass, false));

        var two = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _round = Input("0"); _round.TextChanged += (s, e) => UpdateCommandPreview();
        _fps = Input("30");  _fps.TextChanged += (s, e) => UpdateCommandPreview();
        var roundBox = LabeledInput("圆角 (像素)", "修补 Win11 圆角漏边", _round, false);
        var fpsBox = LabeledInput("轮询频率 (fps)", "兜底同步频率", _fps, false);
        roundBox.Margin = new Thickness(0); fpsBox.Margin = new Thickness(0);
        Grid.SetColumn(roundBox, 0); Grid.SetColumn(fpsBox, 2);
        two.Children.Add(roundBox); two.Children.Add(fpsBox);
        sp.Children.Add(two);

        var checks = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        _full = Check("覆盖整个窗口 (--full)", "包含标题栏/边框，而不仅是客户区");
        _keepWe = Check("退出时保留壁纸窗口 (--keep-we)", "停止时不关闭 WE 窗口");
        _noFallback = Check("禁用自动降级 (--no-fallback)", "失败时直接报错，不尝试下一个模式");
        checks.Children.Add(_full);
        checks.Children.Add(_keepWe);
        checks.Children.Add(_noFallback);
        sp.Children.Add(checks);

        card.Child = sp;
        exp.Content = card;
        return exp;
    }

    // -------------------------------------------------------------- right pane --

    UIElement BuildRightPane()
    {
        var wrap = new Grid { Margin = new Thickness(6, 16, 18, 16) };
        wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status
        wrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // log
        wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // cmd preview
        wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // actions

        // status strip
        var status = new Border { Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10),
                                  Background = Panel, BorderBrush = Stroke, BorderThickness = new Thickness(1) };
        var sSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _statusDot = new Ellipse { Width = 10, Height = 10, Fill = Faint, Margin = new Thickness(0, 0, 10, 0),
                                   VerticalAlignment = VerticalAlignment.Center };
        sSp.Children.Add(_statusDot);
        _statusText = new TextBlock { Text = "空闲", Foreground = Muted, FontWeight = FontWeights.Medium,
                                      VerticalAlignment = VerticalAlignment.Center };
        sSp.Children.Add(_statusText);
        status.Child = sSp;
        Grid.SetRow(status, 0);
        wrap.Children.Add(status);

        // log
        var logCard = new Border { Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(10),
                                   Background = B("#0C0E13"), BorderBrush = Stroke, BorderThickness = new Thickness(1) };
        var logGrid = new Grid();
        logGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        logGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var logHead = new Grid { Margin = new Thickness(12, 8, 8, 6) };
        logHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lh = new TextBlock { Text = "日志", Foreground = Faint, FontSize = 11, FontWeight = FontWeights.SemiBold,
                                 VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lh, 0); logHead.Children.Add(lh);
        var clear = new Button { Content = "清空", Foreground = Muted, Background = Brushes.Transparent,
                                 BorderThickness = new Thickness(0), FontSize = 11, Padding = new Thickness(8, 3, 8, 3),
                                 Cursor = Cursors.Hand };
        clear.Template = FlatTemplate(Brushes.Transparent, B("#20252F"), Text, new CornerRadius(6));
        clear.Click += (s, e) => { _logPara.Inlines.Clear(); };
        Grid.SetColumn(clear, 1); logHead.Children.Add(clear);
        Grid.SetRow(logHead, 0);
        logGrid.Children.Add(logHead);

        _log = new RichTextBox
        {
            IsReadOnly = true, Background = Brushes.Transparent, Foreground = B("#C7CCD6"),
            BorderThickness = new Thickness(0), FontFamily = Mono, FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 0, 8, 10)
        };
        _log.IsDocumentEnabled = false;
        _logPara = new Paragraph { LineHeight = 17, Margin = new Thickness(0) };
        _log.Document = new FlowDocument(_logPara) { PagePadding = new Thickness(0) };
        Grid.SetRow(_log, 1);
        logGrid.Children.Add(_log);
        logCard.Child = logGrid;
        Grid.SetRow(logCard, 1);
        wrap.Children.Add(logCard);

        AppendLog("准备就绪。选择一张壁纸并点击「启动」。", Muted);

        // command preview
        var cmdCard = new Border { Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 9, 12, 9),
                                   CornerRadius = new CornerRadius(10), Background = Panel,
                                   BorderBrush = Stroke, BorderThickness = new Thickness(1) };
        _cmdPreview = new TextBlock { Foreground = Muted, FontFamily = Mono, FontSize = 11,
                                      TextWrapping = TextWrapping.Wrap };
        cmdCard.Child = _cmdPreview;
        Grid.SetRow(cmdCard, 2);
        wrap.Children.Add(cmdCard);

        // actions
        var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _startBtn = AccentButton("启动");
        _startBtn.Height = 44;
        _startBtn.FontSize = 14;
        _startBtn.Click += (s, e) => Start();
        Grid.SetColumn(_startBtn, 0);
        actions.Children.Add(_startBtn);

        _stopBtn = new Button { Content = "停止", Height = 44, Width = 108, Margin = new Thickness(10, 0, 0, 0),
                                FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Text };
        _stopBtn.Template = FlatTemplate(B("#2A2130"), B("#3A2A3A"), Text, new CornerRadius(10),
                                         B("#5A3550"), 1);
        _stopBtn.Click += (s, e) => StopClicked();
        Grid.SetColumn(_stopBtn, 1);
        actions.Children.Add(_stopBtn);

        var actWrap = new StackPanel();
        actWrap.Children.Add(actions);
        _restoreBtn = new Button { Content = "恢复 Codex 窗口  (清理被强杀后的残留)", Height = 34,
                                   Margin = new Thickness(0, 8, 0, 0), Foreground = Muted, FontSize = 12 };
        _restoreBtn.Template = FlatTemplate(Brushes.Transparent, B("#20252F"), Text, new CornerRadius(8), Stroke, 1);
        _restoreBtn.Click += (s, e) => RunRestore();
        actWrap.Children.Add(_restoreBtn);
        Grid.SetRow(actWrap, 3);
        wrap.Children.Add(actWrap);

        return wrap;
    }

    // ------------------------------------------------------------- run / stop --

    void Start()
    {
        if (_running) return;
        if (!File.Exists(HelperPath))
        {
            AppendLog("[!] 未在本程序旁找到 we-codex-bg.exe，请先运行 build.bat。", RedC);
            return;
        }

        var targets = SelectedTargets();
        if (targets.Count == 0)
        {
            AppendLog("[!] 请至少选择一个目标窗口。", RedC);
            return;
        }
        if (targets.Count > 1 && _wallpaper.Text.Trim().Length == 0)
        {
            AppendLog("[!] 多窗口模式需要选择壁纸文件，不能让多个实例接管同一个现有 WE 窗口。", RedC);
            return;
        }

        bool strict = targets.Count > 1;
        int started = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            string weWindow = MakeWeWindowName(targets[i], i, targets.Count);
            if (StartHelper(targets[i], weWindow, strict)) started++;
        }
        if (started == 0) return;

        _running = true; _stopping = false;
        SetRunningUi(true);
        SaveSettings();
        AppendLog("▶ 已启动 " + started + " 个目标  (" + _mode + ")", GreenC);
    }

    bool StartHelper(WinItem target, string weWindow, bool strict)
    {
        var args = BuildArgs(target, weWindow, strict);
        var psi = new ProcessStartInfo(HelperPath, JoinArgs(args))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _appDir
        };
        var session = new HelperSession { Target = target, WeWindow = weWindow };

        try
        {
            session.Process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            session.Process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) Dispatch(() => AppendLog(SessionTag(session) + " " + e.Data, ColorFor(e.Data)));
            };
            session.Process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) Dispatch(() => AppendLog(SessionTag(session) + " " + e.Data, RedC));
            };
            session.Process.Exited += (s, e) => Dispatch(() => OnHelperExited(session));
            session.Process.Start();
            _sessions.Add(session);
            session.Process.BeginOutputReadLine();
            session.Process.BeginErrorReadLine();
            AppendLog(SessionTag(session) + " 启动 helper", GreenC);
            return true;
        }
        catch (Exception ex)
        {
            AppendLog(SessionTag(session) + " [!] 启动失败：" + ex.Message, RedC);
            try { if (session.Process != null) session.Process.Dispose(); } catch { }
            return false;
        }
    }

    void StopClicked()
    {
        if (!_running || _stopping || _sessions.Count == 0) return;
        _stopping = true;
        _stopBtn.IsEnabled = false;
        SetStatus("\u505C\u6B62\u4E2D\u2026", YellowC);
        AppendLog("\u25A0 \u6B63\u5728\u505C\u6B62\u2026", YellowC);

        var sessions = new List<HelperSession>(_sessions);
        var t = new Thread(() => StopAllHelpers(sessions, 5000, true)) { IsBackground = true };
        t.Start();
    }

    // Post WM_CLOSE to every helper first, then wait against one shared deadline.
    // A forced process gets a targeted --restore so another window is never cleaned up by mistake.
    void StopAllHelpers(List<HelperSession> sessions, int timeoutMs, bool writeLog)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        foreach (var session in sessions)
        {
            session.StopRequested = true;
            try
            {
                IntPtr msg = FindHelperMsgWindow(session.Process.Id);
                if (msg != IntPtr.Zero) Native.PostMessage(msg, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        foreach (var session in sessions)
        {
            try
            {
                int remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!session.Process.HasExited && !session.Process.WaitForExit(remaining))
                {
                    if (writeLog) Dispatch(() => AppendLog(SessionTag(session) + " [!] 未正常退出，强制结束并定向恢复", YellowC));
                    try { session.Process.Kill(); } catch { }
                    try { session.Process.WaitForExit(2000); } catch { }
                    RunRestoreSilent(session.Target, session.WeWindow, SessionTag(session));
                }
            }
            catch (Exception ex)
            {
                if (writeLog) Dispatch(() => AppendLog(SessionTag(session) + " [!] 停止出错：" + ex.Message, RedC));
            }
            finally
            {
                session.StopWaitFinished = true;
            }
        }

        // WaitForExit guarantees the Exited event has been raised, but its dispatcher
        // callback may still be queued. Reconcile once so a closed helper cannot leave
        // the UI stuck in the running state; OnHelperExited is deliberately idempotent.
        Dispatch(() =>
        {
            foreach (var session in sessions)
            {
                bool exited = false;
                try { exited = session.Process == null || session.Process.HasExited; } catch { }
                if (exited) OnHelperExited(session);
            }

            // Do not unlock from an individual Exited callback: a forced-stop restore
            // may still be running for another target. Only this coordinator knows that
            // every wait/kill/targeted restore in the batch has completed.
            if (_sessions.Count == 0)
            {
                _running = false; _stopping = false;
                SetRunningUi(false);
            }
            else
            {
                _running = true; _stopping = false;
                _stopBtn.IsEnabled = true;
                SetStatus("仍有 " + _sessions.Count + " 个目标未停止", RedC);
            }
        });
    }

    // Live opacity: post straight to the running helper's message-only window so the
    // change lands immediately.  Restarting to try a different value is useless when
    // you are eyeballing contrast against a moving wallpaper.
    const uint WM_SET_HOST_ALPHA = 0x8000 + 1;
    const uint WM_SET_WALL_ALPHA = 0x8000 + 2;

    void SendLiveAlpha(uint msg, int value)
    {
        if (!_running) return;
        foreach (var session in new List<HelperSession>(_sessions))
        {
            try
            {
                IntPtr h = FindHelperMsgWindow(session.Process.Id);
                if (h != IntPtr.Zero) Native.PostMessage(h, msg, new IntPtr(value), IntPtr.Zero);
            }
            catch { }
        }
    }

    // The C++ helper registers "WeCodexBgMsg", the C# one "WeCodexBgMsgCs".  build.bat
    // picks whichever toolchain exists, so both have to be searched - looking for only
    // one silently degrades graceful stop into kill-and-repair and breaks live opacity.
    static readonly string[] HelperMsgClasses = { "WeCodexBgMsg", "WeCodexBgMsgCs" };

    static IntPtr FindHelperMsgWindow(int pid)
    {
        var HWND_MESSAGE = new IntPtr(-3);
        foreach (string cls in HelperMsgClasses)
        {
            IntPtr after = IntPtr.Zero;
            while (true)
            {
                IntPtr h = Native.FindWindowEx(HWND_MESSAGE, after, cls, null);
                if (h == IntPtr.Zero) break;
                uint wpid;
                Native.GetWindowThreadProcessId(h, out wpid);
                if (wpid == (uint)pid) return h;
                after = h;
            }
        }
        return IntPtr.Zero;
    }

    void OnHelperExited(HelperSession session)
    {
        if (!_sessions.Contains(session))
        {
            if (session.StopWaitFinished)
                try { if (session.Process != null) session.Process.Dispose(); } catch { }
            return;
        }

        int code = 0;
        try { code = session.Process != null ? session.Process.ExitCode : 0; } catch { }
        _sessions.Remove(session);
        AppendLog(SessionTag(session) + " ● 已停止  (退出码 " + code + ")", code == 0 ? Muted : RedC);
        if (!session.StopRequested || session.StopWaitFinished)
            try { if (session.Process != null) session.Process.Dispose(); } catch { }

        if (_sessions.Count == 0)
        {
            if (!_stopping)
            {
                _running = false;
                SetRunningUi(false);
            }
        }
        else if (!_stopping)
        {
            SetStatus("运行中  ·  " + _sessions.Count + " 个目标", GreenC);
        }
    }

    void RunRestore()
    {
        if (!File.Exists(HelperPath)) { AppendLog("[!] \u627E\u4E0D\u5230\u8F85\u52A9\u7A0B\u5E8F exe\u3002", RedC); return; }
        var targets = SelectedTargets();
        if (targets.Count == 0) { AppendLog("[!] 请先选择要恢复的目标窗口。", RedC); return; }
        AppendLog("↺ 正在定向恢复 " + targets.Count + " 个目标…", YellowC);
        var t = new Thread(() =>
        {
            for (int i = 0; i < targets.Count; i++)
            {
                string weWindow = MakeWeWindowName(targets[i], i, targets.Count);
                RunRestoreSilent(targets[i], weWindow, "[恢复 " + TargetName(targets[i]) + "]");
            }
        }) { IsBackground = true };
        t.Start();
    }

    void RunRestoreSilent(WinItem target, string weWindow, string tag)
    {
        try
        {
            var args = new List<string> { "--restore" };
            if (target != null && target.Hwnd != IntPtr.Zero)
            {
                args.Add("--hwnd"); args.Add("0x" + target.Hwnd.ToInt64().ToString("X"));
                args.Add("--pid"); args.Add(target.Pid.ToString());
            }
            if (!string.IsNullOrWhiteSpace(weWindow))
            {
                args.Add("--we-window"); args.Add(weWindow);
                args.Add("--strict-we-window");
            }
            var psi = new ProcessStartInfo(HelperPath, JoinArgs(args))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = _appDir
            };
            var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            foreach (var line in outp.Split('\n'))
            {
                var ln = line.TrimEnd('\r');
                if (ln.Length > 0) Dispatch(() => AppendLog(tag + " " + ln, ColorFor(ln)));
            }
        }
        catch (Exception ex) { Dispatch(() => AppendLog(tag + " [!] 恢复失败：" + ex.Message, RedC)); }
    }

    // ------------------------------------------------------------- args build --

    List<string> BuildArgs(WinItem target, string weWindow, bool strictWeWindow)
    {
        var a = new List<string>();
        string wp = _wallpaper.Text.Trim();
        if (wp.Length > 0) { a.Add("--wallpaper"); a.Add(wp); }

        a.Add("--mode"); a.Add(_mode);
        if (_mode == "composite" || _mode == "alpha") { a.Add("--alpha"); a.Add(((int)_alpha.Value).ToString()); }
        if (_mode == "overlay") { a.Add("--film"); a.Add(((int)_film.Value).ToString()); }
        else if ((int)_wallAlpha.Value != 255) { a.Add("--wall-alpha"); a.Add(((int)_wallAlpha.Value).ToString()); }

        string we = _we.Text.Trim();
        if (we.Length > 0) { a.Add("--we"); a.Add(we); }

        if (!string.IsNullOrWhiteSpace(weWindow) && weWindow != "CodexWallpaperHost")
        { a.Add("--we-window"); a.Add(weWindow); }
        if (strictWeWindow) a.Add("--strict-we-window");

        string cc = _contentClass.Text.Trim();
        if (cc.Length > 0) { a.Add("--content-class"); a.Add(cc); }

        int round = ParseInt(_round.Text, 0);
        if (round > 0) { a.Add("--round"); a.Add(round.ToString()); }
        int fps = ParseInt(_fps.Text, 30);
        if (fps != 30) { a.Add("--fps"); a.Add(fps.ToString()); }

        if (_full.IsChecked == true) a.Add("--full");
        if (_keepWe.IsChecked == true) a.Add("--keep-we");
        if (_noFallback.IsChecked == true) a.Add("--no-fallback");

        if (target != null && target.Hwnd != IntPtr.Zero)
        {
            a.Add("--hwnd"); a.Add("0x" + target.Hwnd.ToInt64().ToString("X"));
            a.Add("--pid"); a.Add(target.Pid.ToString());
        }

        a.Add("-v");
        return a;
    }

    void UpdateCommandPreview()
    {
        if (_cmdPreview == null) return;
        var targets = SelectedTargets();
        if (targets.Count == 0) { _cmdPreview.Text = "未选择目标窗口"; return; }
        var sb = new StringBuilder();
        if (targets.Count > 1) sb.Append("将启动 ").Append(targets.Count).Append(" 个 helper：\n");
        int shown = Math.Min(targets.Count, 3);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append('\n');
            string weWindow = MakeWeWindowName(targets[i], i, targets.Count);
            sb.Append("we-codex-bg.exe ").Append(JoinArgs(BuildArgs(targets[i], weWindow, targets.Count > 1)));
        }
        if (targets.Count > shown) sb.Append("\n…其余 ").Append(targets.Count - shown).Append(" 个目标使用相同参数");
        _cmdPreview.Text = sb.ToString();
    }

    string MakeWeWindowName(WinItem target, int index, int total)
    {
        string baseName = _weWindow != null ? _weWindow.Text.Trim() : "";
        if (baseName.Length == 0) baseName = "CodexWallpaperHost";
        if (total <= 1) return baseName;
        string suffix = target != null && target.Hwnd != IntPtr.Zero
            ? target.Hwnd.ToInt64().ToString("X") : (index + 1).ToString();
        return baseName + "-" + suffix;
    }

    static string TargetName(WinItem target)
    {
        if (target == null || target.IsAuto) return "Codex 自动检测";
        string title = target.Title ?? target.Exe ?? "窗口";
        if (title.Length > 22) title = title.Substring(0, 21) + "…";
        return title + " · " + target.Pid;
    }

    static string SessionTag(HelperSession session)
    {
        return "[" + TargetName(session != null ? session.Target : null) + "]";
    }

    static string JoinArgs(List<string> a)
    {
        var sb = new StringBuilder();
        foreach (var s in a)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (s.Length == 0 || s.IndexOf(' ') >= 0 || s.IndexOf('"') >= 0)
                sb.Append('"').Append(s.Replace("\"", "\\\"")).Append('"');
            else sb.Append(s);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------- state --

    void SetRunningUi(bool running)
    {
        _startBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _stopBtn.IsEnabled = running;
        _stopBtn.Opacity = running ? 1 : 0.45;
        _restoreBtn.IsEnabled = !running;
        // Lock the settings that only take effect at launch.  The opacity sliders stay
        // live on purpose: they are pushed to the running helper and are exactly what
        // you need to tune while watching the result.
        foreach (var c in new Control[] { _wallpaper, _we, _weWindow, _contentClass, _round, _fps,
                                          _full, _keepWe, _noFallback })
            if (c != null) c.IsEnabled = !running;
        if (_targetControls != null) _targetControls.IsEnabled = !running;
        foreach (var card in _modeCards) card.IsHitTestVisible = !running;

        if (running) SetStatus("运行中  ·  " + _sessions.Count + " 个目标  ·  " + _mode, GreenC);
        else SetStatus("空闲", Muted);
    }

    void SetStatus(string text, Brush color)
    {
        _statusText.Text = text;
        _statusText.Foreground = color == Muted ? Muted : Text;
        _statusDot.Fill = color;
    }

    Brush ColorFor(string line)
    {
        if (line.StartsWith("[!]")) return RedC;
        if (line.StartsWith("[we]")) return Faint;
        if (line.StartsWith("[i]") && (line.Contains("已恢复") || line.Contains("restored"))) return GreenC;
        if (line.StartsWith("[i]")) return B("#9DB2D6");
        return B("#C7CCD6");
    }

    void AppendLog(string line, Brush color)
    {
        _logPara.Inlines.Add(new Run(line) { Foreground = color });
        _logPara.Inlines.Add(new LineBreak());
        _log.ScrollToEnd();
    }

    void Dispatch(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------ persistence --

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(CfgPath));
            var sb = new StringBuilder();
            sb.AppendLine("wallpaper=" + _wallpaper.Text);
            sb.AppendLine("we=" + _we.Text);
            sb.AppendLine("weWindow=" + _weWindow.Text);
            sb.AppendLine("contentClass=" + _contentClass.Text);
            sb.AppendLine("round=" + _round.Text);
            sb.AppendLine("fps=" + _fps.Text);
            sb.AppendLine("cfgver=3");
            sb.AppendLine("mode=" + _mode);
            sb.AppendLine("alpha=" + (int)_alpha.Value);
            sb.AppendLine("film=" + (int)_film.Value);
            sb.AppendLine("wallAlpha=" + (int)_wallAlpha.Value);
            sb.AppendLine("full=" + (_full.IsChecked == true));
            sb.AppendLine("keepWe=" + (_keepWe.IsChecked == true));
            sb.AppendLine("noFallback=" + (_noFallback.IsChecked == true));
            File.WriteAllText(CfgPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(CfgPath)) return;
            var map = new Dictionary<string, string>();
            foreach (var raw in File.ReadAllLines(CfgPath))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                map[raw.Substring(0, eq)] = raw.Substring(eq + 1);
            }
            string v;
            if (map.TryGetValue("wallpaper", out v)) _wallpaper.Text = v;
            if (map.TryGetValue("we", out v)) _we.Text = v;
            if (map.TryGetValue("weWindow", out v) && v.Length > 0) _weWindow.Text = v;
            if (map.TryGetValue("contentClass", out v)) _contentClass.Text = v;
            if (map.TryGetValue("round", out v)) _round.Text = v;
            if (map.TryGetValue("fps", out v)) _fps.Text = v;
            if (map.TryGetValue("mode", out v) && v.Length > 0) _mode = v;

            // One-time migration: builds before cfgver=2 defaulted to composite, which
            // leaves Electron hosts (Codex/ChatGPT) rendering but unclickable.  Anyone
            // carrying that saved setting gets moved to the safe default exactly once.
            string ver;
            int cfgver = map.TryGetValue("cfgver", out ver) ? ParseInt(ver, 1) : 1;
            if (cfgver < 2 && _mode == "composite") _mode = "alpha";
            // cfgver 3: the old 205 host opacity washes the UI out under a bright
            // wallpaper.  Nudge anyone still on it up to the readable default.
            if (cfgver < 3 && (int)_alpha.Value <= 205) _alpha.Value = 235;
            if (map.TryGetValue("alpha", out v)) _alpha.Value = ParseInt(v, 235);
            if (map.TryGetValue("film", out v)) _film.Value = ParseInt(v, 70);
            if (map.TryGetValue("wallAlpha", out v)) _wallAlpha.Value = ParseInt(v, 255);
            if (map.TryGetValue("full", out v)) _full.IsChecked = v == "True";
            if (map.TryGetValue("keepWe", out v)) _keepWe.IsChecked = v == "True";
            if (map.TryGetValue("noFallback", out v)) _noFallback.IsChecked = v == "True";
        }
        catch { }
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        SaveSettings();
        if (_running && _sessions.Count > 0)
        {
            _stopping = true;
            StopAllHelpers(new List<HelperSession>(_sessions), 4000, false);
        }
    }

    void CreateTrayIcon()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _appIcon ?? System.Drawing.SystemIcons.Application,
            Text = "WE · Codex 背景",
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = new System.Windows.Forms.ToolStripMenuItem("打开");
        open.Click += (s, e) => RestoreFromTray();
        var exit = new System.Windows.Forms.ToolStripMenuItem("退出");
        exit.Click += (s, e) => { _exitRequested = true; Close(); };
        menu.Items.Add(open);
        menu.Items.Add(exit);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => RestoreFromTray();
    }

    void ApplyAppIcon()
    {
        try
        {
            using (var process = Process.GetCurrentProcess())
                _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(process.MainModule.FileName);
            if (_appIcon == null) _appIcon = System.Drawing.SystemIcons.Application;
            Icon = Imaging.CreateBitmapSourceFromHIcon(_appIcon.Handle, Int32Rect.Empty,
                                                        BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            _appIcon = System.Drawing.SystemIcons.Application;
        }
    }

    void HideToTray()
    {
        Hide();
    }

    void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // -------------------------------------------------------- widget factories --

    static SolidColorBrush B(string hex) { return new SolidColorBrush(C(hex)); }
    static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
    static int ParseInt(string s, int def) { int v; return int.TryParse((s ?? "").Trim(), out v) ? v : def; }

    Border Card()
    {
        var b = new Border
        {
            Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12), Background = Panel,
            BorderBrush = Stroke, BorderThickness = new Thickness(1)
        };
        b.Child = new StackPanel();
        return b;
    }

    UIElement SectionHeader(string title, string subtitle)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Text });
        if (subtitle != null)
            sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = Faint, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    TextBox Input(string text)
    {
        var tb = new TextBox
        {
            Text = text, Height = 38, Background = Panel2, Foreground = Text,
            CaretBrush = Text, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12.5
        };
        tb.GotKeyboardFocus += (s, e) => tb.BorderBrush = Accent;
        tb.LostKeyboardFocus += (s, e) => tb.BorderBrush = Stroke;
        return tb;
    }

    FrameworkElement LabeledInput(string label, string hint, TextBox input, bool withBrowse)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        wrap.Children.Add(new TextBlock { Text = label, Foreground = Text, FontWeight = FontWeights.Medium, FontSize = 12.5 });
        if (hint != null)
            wrap.Children.Add(new TextBlock { Text = hint, Foreground = Faint, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 5) });
        else
            input.Margin = new Thickness(0, 5, 0, 0);

        if (withBrowse)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(input, 0);
            row.Children.Add(input);
            var br = SecondaryButton("\u2026");
            br.Width = 44; br.Margin = new Thickness(8, 0, 0, 0);
            br.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "wallpaper64.exe|wallpaper64.exe|可执行文件 (*.exe)|*.exe" };
                if (dlg.ShowDialog(this) == true) { input.Text = dlg.FileName; UpdateCommandPreview(); }
            };
            Grid.SetColumn(br, 1);
            row.Children.Add(br);
            wrap.Children.Add(row);
        }
        else wrap.Children.Add(input);
        return wrap;
    }

    CheckBox Check(string text, string hint)
    {
        var cb = new CheckBox { Foreground = Text, Margin = new Thickness(0, 0, 0, 9), Cursor = Cursors.Hand };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = text, Foreground = Text, FontSize = 12.5 });
        if (hint != null) sp.Children.Add(new TextBlock { Text = hint, Foreground = Faint, FontSize = 10.5 });
        cb.Content = sp;
        cb.Checked += (s, e) => UpdateCommandPreview();
        cb.Unchecked += (s, e) => UpdateCommandPreview();
        return cb;
    }

    Slider MakeSlider(double min, double max, double val)
    {
        return new Slider
        {
            Minimum = min, Maximum = max, Value = val, SmallChange = 1, LargeChange = 10,
            IsSnapToTickEnabled = true, TickFrequency = 1, Height = 26,
            Foreground = Accent, VerticalAlignment = VerticalAlignment.Center
        };
    }

    TextBlock ValueBadge(string text)
    {
        return new TextBlock
        {
            Text = text, Foreground = Accent, FontWeight = FontWeights.SemiBold, FontFamily = Mono,
            FontSize = 13, MinWidth = 34, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    Button AccentButton(string text)
    {
        var b = new Button { Content = text, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                             Height = 40, Cursor = Cursors.Hand };
        b.Template = FlatTemplate(Accent, AccentHi, Brushes.White, new CornerRadius(10));
        return b;
    }

    Button SecondaryButton(string text)
    {
        var b = new Button { Content = text, Foreground = Text, Height = 38, Cursor = Cursors.Hand, FontSize = 12.5 };
        b.Template = FlatTemplate(Panel2, B("#252B37"), Text, new CornerRadius(8), Stroke, 1);
        return b;
    }

    // A minimal flat button template: rounded Border + centered content, with
    // hover / press / disabled visual states.
    static ControlTemplate FlatTemplate(Brush bg, Brush hover, Brush fg, CornerRadius radius)
    {
        return FlatTemplate(bg, hover, fg, radius, Brushes.Transparent, 0);
    }
    static ControlTemplate FlatTemplate(Brush bg, Brush hover, Brush fg, CornerRadius radius, Brush border, double borderTh)
    {
        var t = new ControlTemplate(typeof(ButtonBase));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, bg);
        bd.SetValue(Border.CornerRadiusProperty, radius);
        bd.SetValue(Border.BorderBrushProperty, border);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(borderTh));

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 8, 0));
        bd.AppendChild(cp);
        t.VisualTree = bd;

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
        t.Triggers.Add(over);

        var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
        press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "bd"));
        t.Triggers.Add(press);

        var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "bd"));
        t.Triggers.Add(dis);

        return t;
    }
}

// -------------------------------------------------------------------- win32 --

internal static class Native
{
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr proc, uint flags, StringBuilder path, ref uint size);

    public static string Text(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        return sb.ToString();
    }

    public static string ExeName(uint pid)
    {
        IntPtr p = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
        if (p == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(600);
            uint n = (uint)sb.Capacity;
            if (!QueryFullProcessImageNameW(p, 0, sb, ref n)) return "";
            string full = sb.ToString();
            int slash = full.LastIndexOfAny(new[] { '\\', '/' });
            return (slash >= 0 ? full.Substring(slash + 1) : full).ToLowerInvariant();
        }
        finally { CloseHandle(p); }
    }
}

// Windows volume mixer access.  Only audio sessions belonging to Wallpaper Engine
// renderer processes are changed; the host application is never considered.
internal static class WallpaperAudio
{
    const uint CLSCTX_ALL = 23;

    enum EDataFlow { Render = 0 }
    enum ERole { Multimedia = 1 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl control);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid value);
        [PreserveSig] int SetGroupingParam(ref Guid value, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid value);
        [PreserveSig] int SetGroupingParam(ref Guid value, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid context);
        [PreserveSig] int GetMute(out bool mute);
    }

    static bool IsWallpaperProcess(uint pid)
    {
        try
        {
            using (var p = Process.GetProcessById((int)pid))
            {
                string n = p.ProcessName.ToLowerInvariant();
                return n == "wallpaper32" || n == "wallpaper64" ||
                       n == "webwallpaper32" || n == "webwallpaper64" ||
                       n == "wallpaperwindows" ||
                       n == "wallpaperservice32_engine" || n == "wallpaperservice64_engine";
            }
        }
        catch { return false; }
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    static void Release(object value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public static int SetVolume(float level)
    {
        object enumerator = null, manager = null;
        IMMDevice device = null;
        IAudioSessionEnumerator sessions = null;
        int changed = 0;
        try
        {
            enumerator = new MMDeviceEnumeratorComObject();
            Check(((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));

            Guid managerId = typeof(IAudioSessionManager2).GUID;
            Check(device.Activate(ref managerId, CLSCTX_ALL, IntPtr.Zero, out manager));
            Check(((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions));

            int count;
            Check(sessions.GetCount(out count));
            Guid context = Guid.Empty;
            for (int i = 0; i < count; ++i)
            {
                IAudioSessionControl control = null;
                try
                {
                    Check(sessions.GetSession(i, out control));
                    var control2 = control as IAudioSessionControl2;
                    uint pid;
                    if (control2 == null || control2.GetProcessId(out pid) < 0 || !IsWallpaperProcess(pid)) continue;
                    var volume = control as ISimpleAudioVolume;
                    if (volume == null) continue;
                    Check(volume.SetMasterVolume(Math.Max(0f, Math.Min(1f, level)), ref context));
                    ++changed;
                }
                finally { Release(control); }
            }
            return changed;
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
            Release(enumerator);
        }
    }
}
