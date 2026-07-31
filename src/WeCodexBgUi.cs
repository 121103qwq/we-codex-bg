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
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
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
    static readonly bool EnableWallpaperPropertyControl = false;

    // ---------------------------------------------------------------- controls --

    TextBox _wallpaper, _we, _weWindow, _contentClass, _round, _fps;
    TextBox _wpSearch;
    ListBox _wpList;
    TextBlock _wpCount;
    StackPanel _propHost = null;       // dynamic wallpaper-property controls disabled
    TextBlock _propHint = null;
    Slider _volume;
    TextBlock _volumeVal;
    Slider  _alpha, _film, _wallAlpha, _sideAlpha, _inputAlpha;
    TextBlock _alphaVal, _filmVal, _wallAlphaVal, _sideAlphaVal, _inputAlphaVal, _maskColorText, _statusText, _cmdPreview;
    Border  _alphaRow, _filmRow, _wallAlphaRow, _sideAlphaRow, _inputAlphaRow, _embedNote;
    CheckBox _full, _keepWe, _noFallback;
    ComboBox _target;
    Button  _startBtn, _stopBtn, _restoreBtn;
    RichTextBox _log;
    Paragraph _logPara;
    Ellipse _statusDot;
    readonly List<Border> _modeCards = new List<Border>();

    // ------------------------------------------------------------------ state --

    string _mode = "alpha";       // safe default: never touches the content child
    string _maskColor = "12161E";
    readonly List<WallpaperItem> _library = new List<WallpaperItem>();
    readonly object _themeLock = new object();
    bool _autoDarkThemeActive;
    int _toneCheckVersion;
    bool _suppressListSelect;      // set while the list is rebuilt programmatically
    Process _proc;
    volatile bool _running;
    volatile bool _stopping;

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
        btns.Children.Add(CaptionButton("\uE8BB", true,  Close));
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
            if (w != null)
            {
                _maskColor = w.MaskColor;
                if (_maskColorText != null) _maskColorText.Text = "#" + _maskColor;
                _wallpaper.Text = w.ProjectPath;
                UpdateCommandPreview();
            }
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
            UpdateMaskColorForPath(_wallpaper.Text.Trim());
            UpdateCommandPreview();
            if (EnableWallpaperPropertyControl && _propHost != null)
                LoadProperties(_wallpaper.Text.Trim());
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
        public string MaskColor = "12161E";
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
                UpdateMaskColorForPath(_wallpaper.Text.Trim());
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
                string img = IOPath.Combine(dir, v);
                if (File.Exists(img))
                {
                    w.Thumb = LoadThumb(img);
                    w.MaskColor = DominantMaskColor(w.Thumb as BitmapSource);
                }
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

    static ImageSource LoadThumb(string file)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(file, UriKind.Absolute);
            bi.DecodePixelWidth = 96;                 // keep memory small
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            bi.Freeze();                              // required to cross threads
            return bi;
        }
        catch { return null; }
    }

    static string DominantMaskColor(BitmapSource src)
    {
        if (src == null) return "12161E";
        try
        {
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int stride = bgra.PixelWidth * 4;
            var px = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(px, stride, 0);
            var bins = new Dictionary<int, int[]>();
            for (int y = 0; y < bgra.PixelHeight; y += 2)
            for (int x = 0; x < bgra.PixelWidth; x += 2)
            {
                int p = y * stride + x * 4;
                if (px[p + 3] < 128) continue;
                int b = px[p], g = px[p + 1], r = px[p + 2];
                int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                int[] v;
                if (!bins.TryGetValue(key, out v)) bins[key] = v = new int[4];
                v[0] += r; v[1] += g; v[2] += b; v[3]++;
            }
            int[] best = null;
            foreach (var v in bins.Values) if (best == null || v[3] > best[3]) best = v;
            if (best == null || best[3] == 0) return "12161E";
            double r0 = best[0] / (double)best[3], g0 = best[1] / (double)best[3], b0 = best[2] / (double)best[3];
            double luma = r0 * .2126 + g0 * .7152 + b0 * .0722;
            double f = luma > 1 ? Math.Min(.32, 28 / luma) : .25;
            int r1 = Math.Max(8, Math.Min(62, (int)(r0 * f)));
            int g1 = Math.Max(8, Math.Min(62, (int)(g0 * f)));
            int b1 = Math.Max(8, Math.Min(62, (int)(b0 * f)));
            return r1.ToString("X2") + g1.ToString("X2") + b1.ToString("X2");
        }
        catch { return "12161E"; }
    }

    void UpdateMaskColorForPath(string path)
    {
        foreach (var w in _library)
        {
            if (!string.Equals(w.ProjectPath, path, StringComparison.OrdinalIgnoreCase)) continue;
            _maskColor = w.MaskColor;
            if (_maskColorText != null) _maskColorText.Text = "自动遮罩色  #" + _maskColor;
            UpdateCommandPreview();
            return;
        }
    }

    static bool IsDarkWallpaper(BitmapSource src)
    {
        if (src == null) return false;
        try
        {
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int stride = bgra.PixelWidth * 4;
            var px = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(px, stride, 0);
            var bins = new Dictionary<int, int>();
            long lumaSum = 0;
            int samples = 0, darkSamples = 0;
            for (int y = 0; y < bgra.PixelHeight; y += 3)
            for (int x = 0; x < bgra.PixelWidth; x += 3)
            {
                int p = y * stride + x * 4;
                if (px[p + 3] < 128) continue;
                int b = px[p], g = px[p + 1], r = px[p + 2];
                int luma = (int)Math.Round(r * .2126 + g * .7152 + b * .0722);
                int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                bins[key] = bins.ContainsKey(key) ? bins[key] + 1 : 1;
                lumaSum += luma;
                samples++;
                if (luma < 190) darkSamples++;
            }
            if (samples == 0) return false;
            int dominantKey = 0, dominantCount = 0;
            foreach (var item in bins)
                if (item.Value > dominantCount) { dominantKey = item.Key; dominantCount = item.Value; }
            int dr = ((dominantKey >> 8) & 15) * 17 + 8;
            int dg = ((dominantKey >> 4) & 15) * 17 + 8;
            int db = (dominantKey & 15) * 17 + 8;
            double dominantLuma = dr * .2126 + dg * .7152 + db * .0722;
            double averageLuma = lumaSum / (double)samples;
            double darkShare = darkSamples / (double)samples;
            return dominantLuma < 188 || averageLuma < 172 || darkShare > .58;
        }
        catch { return false; }
    }

    static BitmapSource WallpaperPreview(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (string name in new[] { "preview.jpg", "preview.png", "preview.gif" })
                {
                    string candidate = IOPath.Combine(path, name);
                    if (File.Exists(candidate)) return LoadThumb(candidate) as BitmapSource;
                }
                return null;
            }
            if (!File.Exists(path)) return null;
            string file = path;
            if (string.Equals(IOPath.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                var top = JsonTopLevelStrings(File.ReadAllText(path, Encoding.UTF8));
                string preview;
                if (!top.TryGetValue("preview", out preview) || preview.Length == 0) return null;
                file = IOPath.Combine(IOPath.GetDirectoryName(path), preview);
            }
            return File.Exists(file) ? LoadThumb(file) as BitmapSource : null;
        }
        catch { return null; }
    }

    void CheckWallpaperToneAsync(string path)
    {
        int version = Interlocked.Increment(ref _toneCheckVersion);
        BitmapSource cached = null;
        foreach (var w in _library)
            if (string.Equals(w.ProjectPath, path, StringComparison.OrdinalIgnoreCase))
            {
                cached = w.Thumb as BitmapSource;
                break;
            }
        var t = new Thread(() =>
        {
            BitmapSource preview = cached ?? WallpaperPreview(path);
            if (!IsDarkWallpaper(preview)) return;
            Dispatch(() =>
            {
                if (version != _toneCheckVersion || !_running) return;
                try
                {
                    ApplyAutoDarkMode();
                    AppendLog("[i] 已根据壁纸主色切换深色模式。", GreenC);
                    MessageBox.Show(this, "深色模式在该壁纸中表现的通常更好", "壁纸主题提示",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { AppendLog("[!] 深色模式切换失败：" + ex.Message, RedC); }
            });
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
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
    // Everything here drives the running wallpaper through Wallpaper Engine's own
    // CLI (`wallpaper64.exe -control ...`).  We never poke the renderer ourselves.

    UIElement BuildControlSection()
    {
        var card = Card();
        var sp = (StackPanel)card.Child;
        sp.Children.Add(SectionHeader("控制", "播放控制"));

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
        // only push on release: WE spawns a process per call, so don't fire per pixel
        _volume.PreviewMouseUp += (s, e) => WeControl("volume", "-value", ((int)_volume.Value).ToString());
        sp.Children.Add(SliderRow("音量", "拖动结束后应用", _volume, _volumeVal));

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
        if (!EnableWallpaperPropertyControl) return;
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
        if (!EnableWallpaperPropertyControl) return;
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
        grid.Children.Add(ModeCard("adaptive", "自适应 · Adaptive DEV", "壁纸位于下方；侧栏和输入区由独立遮罩提高不透明度，并按壁纸主色自动配色。"));
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
        bool showAlpha = _mode == "composite" || _mode == "alpha" || _mode == "adaptive";
        bool showFilm = _mode == "overlay";
        bool adaptive = _mode == "adaptive";
        _alphaRow.Visibility = showAlpha ? Visibility.Visible : Visibility.Collapsed;
        _filmRow.Visibility = showFilm ? Visibility.Visible : Visibility.Collapsed;
        // the wallpaper is layered in every non-overlay mode, so it can always be dimmed
        _wallAlphaRow.Visibility = showFilm ? Visibility.Collapsed : Visibility.Visible;
        _sideAlphaRow.Visibility = adaptive ? Visibility.Visible : Visibility.Collapsed;
        _inputAlphaRow.Visibility = adaptive ? Visibility.Visible : Visibility.Collapsed;
        if (_maskColorText != null) _maskColorText.Visibility = adaptive ? Visibility.Visible : Visibility.Collapsed;
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

        _sideAlpha = MakeSlider(0, 255, 220);
        _sideAlphaVal = ValueBadge("220");
        _sideAlphaRow = SliderRow("侧边栏遮罩", "越高 = 侧栏越接近不透明", _sideAlpha, _sideAlphaVal);
        _sideAlpha.ValueChanged += (s, e) =>
        {
            _sideAlphaVal.Text = ((int)_sideAlpha.Value).ToString();
            UpdateCommandPreview();
            SendLiveAlpha(WM_SET_SIDE_ALPHA, (int)_sideAlpha.Value);
        };
        sp.Children.Add(_sideAlphaRow);

        _inputAlpha = MakeSlider(0, 255, 245);
        _inputAlphaVal = ValueBadge("245");
        _inputAlphaRow = SliderRow("输入区遮罩", "越高 = 输入框区域越少显示壁纸", _inputAlpha, _inputAlphaVal);
        _inputAlpha.ValueChanged += (s, e) =>
        {
            _inputAlphaVal.Text = ((int)_inputAlpha.Value).ToString();
            UpdateCommandPreview();
            SendLiveAlpha(WM_SET_INPUT_ALPHA, (int)_inputAlpha.Value);
        };
        sp.Children.Add(_inputAlphaRow);

        _maskColorText = new TextBlock
        {
            Text = "#" + _maskColor, Foreground = Muted, FontFamily = Mono,
            FontSize = 11, Margin = new Thickness(2, 6, 0, 0)
        };
        sp.Children.Add(_maskColorText);

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
        sp.Children.Add(SectionHeader("目标窗口", "让壁纸位于哪个窗口背后"));

        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _target = new ComboBox
        {
            Height = 38, Background = Panel2, Foreground = Text, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _target.SelectionChanged += (s, e) => UpdateCommandPreview();
        Grid.SetColumn(_target, 0);
        row.Children.Add(_target);

        var refresh = SecondaryButton("刷新");
        refresh.Margin = new Thickness(8, 0, 0, 0);
        refresh.Width = 92;
        refresh.Click += (s, e) => RefreshTargets();
        Grid.SetColumn(refresh, 1);
        row.Children.Add(refresh);

        sp.Children.Add(row);
        return card;
    }

    sealed class WinItem
    {
        public string Display { get; set; }
        public uint Pid { get; set; }
        public override string ToString() { return Display; }
    }

    void RefreshTargets()
    {
        var items = new List<WinItem> { new WinItem { Display = "自动检测  (Codex / ChatGPT)", Pid = 0 } };
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
            string disp = title;
            if (disp.Length > 46) disp = disp.Substring(0, 45) + "\u2026";
            items.Add(new WinItem { Display = disp + "   ·   " + exe, Pid = pid });
            return true;
        }, IntPtr.Zero);

        _target.ItemsSource = items;
        _target.SelectedIndex = 0;
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
        sp.Children.Add(LabeledInput("宿主窗口名称", "-playInWindow 使用的名称", _weWindow, false));

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

        var args = BuildArgs();
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

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) Dispatch(() => AppendLog(e.Data, ColorFor(e.Data))); };
            _proc.ErrorDataReceived  += (s, e) => { if (e.Data != null) Dispatch(() => AppendLog(e.Data, RedC)); };
            _proc.Exited += (s, e) => Dispatch(OnProcExited);
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendLog("[!] \u542F\u52A8\u5931\u8D25\uFF1A" + ex.Message, RedC);
            _proc = null;
            return;
        }

        _running = true; _stopping = false;
        SetRunningUi(true);
        SaveSettings();
        AppendLog("\u25B6 \u5DF2\u542F\u52A8  (" + _mode + ")", GreenC);
        CheckWallpaperToneAsync(_wallpaper.Text.Trim());
    }

    void StopClicked()
    {
        if (!_running || _stopping || _proc == null) return;
        Interlocked.Increment(ref _toneCheckVersion);
        _stopping = true;
        _stopBtn.IsEnabled = false;
        SetStatus("\u505C\u6B62\u4E2D\u2026", YellowC);
        AppendLog("\u25A0 \u6B63\u5728\u505C\u6B62\u2026", YellowC);

        var proc = _proc;
        var t = new Thread(() => GracefulStop(proc, 5000)) { IsBackground = true };
        t.Start();
    }

    // Post WM_CLOSE to the helper's message-only window so it runs RestoreAll();
    // if it doesn't exit in time, kill it and run --restore to clean up.
    void GracefulStop(Process proc, int timeoutMs)
    {
        try
        {
            IntPtr msg = FindHelperMsgWindow(proc.Id);
            if (msg != IntPtr.Zero) Native.PostMessage(msg, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);

            if (!proc.WaitForExit(timeoutMs))
            {
                Dispatch(() => AppendLog("[!] 未正常退出 —— 强制结束并执行 --restore", YellowC));
                try { proc.Kill(); } catch { }
                try { proc.WaitForExit(2000); } catch { }
                RunRestoreSilent();
            }
        }
        catch (Exception ex) { Dispatch(() => AppendLog("[!] 停止出错：" + ex.Message, RedC)); }
    }

    // Live opacity: post straight to the running helper's message-only window so the
    // change lands immediately.  Restarting to try a different value is useless when
    // you are eyeballing contrast against a moving wallpaper.
    const uint WM_SET_HOST_ALPHA = 0x8000 + 1;
    const uint WM_SET_WALL_ALPHA = 0x8000 + 2;
    const uint WM_SET_SIDE_ALPHA = 0x8000 + 3;
    const uint WM_SET_INPUT_ALPHA = 0x8000 + 4;

    void SendLiveAlpha(uint msg, int value)
    {
        if (!_running || _proc == null) return;
        try
        {
            IntPtr h = FindHelperMsgWindow(_proc.Id);
            if (h != IntPtr.Zero) Native.PostMessage(h, msg, new IntPtr(value), IntPtr.Zero);
        }
        catch { }
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

    void OnProcExited()
    {
        int code = 0;
        try { code = _proc != null ? _proc.ExitCode : 0; } catch { }
        _running = false; _stopping = false;
        SetRunningUi(false);
        AppendLog("\u25CF \u5DF2\u505C\u6B62  (\u9000\u51FA\u7801 " + code + ")", code == 0 ? Muted : RedC);
        try { if (_proc != null) _proc.Dispose(); } catch { }
        _proc = null;
        if (_autoDarkThemeActive)
            new Thread(RemoveAutoDarkMode) { IsBackground = true }.Start();
    }

    void RunRestore()
    {
        if (!File.Exists(HelperPath)) { AppendLog("[!] \u627E\u4E0D\u5230\u8F85\u52A9\u7A0B\u5E8F exe\u3002", RedC); return; }
        AppendLog("\u21BA \u6B63\u5728\u6267\u884C --restore\u2026", YellowC);
        var t = new Thread(RunRestoreSilent) { IsBackground = true };
        t.Start();
    }

    void RunRestoreSilent()
    {
        try
        {
            var psi = new ProcessStartInfo(HelperPath, "--restore")
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
                if (ln.Length > 0) Dispatch(() => AppendLog(ln, ColorFor(ln)));
            }
        }
        catch (Exception ex) { Dispatch(() => AppendLog("[!] 恢复失败：" + ex.Message, RedC)); }
    }

    void ApplyAutoDarkMode()
    {
        string css = @"
:root {
  color-scheme: dark !important;
  --color-token-foreground: #f5f7ff !important;
  --color-token-icon-foreground: #f5f7ff !important;
  --color-token-description-foreground: rgba(238,242,255,.76) !important;
  --color-token-input-foreground: #f7f9ff !important;
  --color-token-input-placeholder-foreground: rgba(238,242,255,.62) !important;
  --color-token-text-code-block-background: rgba(5,9,18,.76) !important;
  --color-token-text-preformat-background: rgba(5,9,18,.82) !important;
  --color-token-text-preformat-foreground: #f7f9ff !important;
  --color-token-border-default: rgba(255,255,255,.18) !important;
}
html, body { background: #0f1116 !important; color: #f5f7ff !important; }
.main-surface { background: rgba(7,11,20,.94) !important; }
.app-shell-left-panel { background: rgba(5,9,18,.92) !important; }
.composer-surface-chrome { background: rgba(20,24,34,.94) !important; color: #f7f9ff !important; }";
        string expression = "(() => { let s=document.getElementById('we-codex-auto-dark');" +
            "if(!s){s=document.createElement('style');s.id='we-codex-auto-dark';document.documentElement.appendChild(s);}" +
            "s.textContent=\"" + JVal.Escape(css) + "\";return true;})()";
        lock (_themeLock)
        {
            CdpEvaluate(expression);
            _autoDarkThemeActive = true;
        }
    }

    void RemoveAutoDarkMode()
    {
        lock (_themeLock)
        {
            if (!_autoDarkThemeActive) return;
            try { CdpEvaluate("(() => { const s=document.getElementById('we-codex-auto-dark');if(s)s.remove();return true;})()"); }
            catch { }
            _autoDarkThemeActive = false;
        }
    }

    static void CdpEvaluate(string expression)
    {
        string wsUrl = FindCodexWebSocket();
        using (var ws = new ClientWebSocket())
        using (var cancel = new CancellationTokenSource(4000))
        {
            ws.ConnectAsync(new Uri(wsUrl), cancel.Token).GetAwaiter().GetResult();
            string request = "{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"" +
                             JVal.Escape(expression) + "\",\"returnByValue\":true}}";
            byte[] send = Encoding.UTF8.GetBytes(request);
            ws.SendAsync(new ArraySegment<byte>(send), WebSocketMessageType.Text, true, cancel.Token)
              .GetAwaiter().GetResult();
            var buffer = new byte[16384];
            while (true)
            {
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult part;
                    do
                    {
                        part = ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancel.Token)
                                 .GetAwaiter().GetResult();
                        if (part.MessageType == WebSocketMessageType.Close)
                            throw new Exception("CDP 连接已关闭");
                        ms.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    JVal msg = JVal.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                    if (msg["id"] == null || (int)msg["id"].AsNumber() != 1) continue;
                    if (msg["error"] != null)
                        throw new Exception(msg["error"]["message"].AsString("CDP 执行失败"));
                    if (msg["result"] != null && msg["result"]["exceptionDetails"] != null)
                        throw new Exception("Codex 样式注入失败");
                    return;
                }
            }
        }
    }

    static string FindCodexWebSocket()
    {
        foreach (int port in new[] { 9229, 9222 })
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
                req.Timeout = 1200;
                string json;
                using (var response = req.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    json = reader.ReadToEnd();
                JVal list = JVal.Parse(json);
                foreach (JVal target in list.Array)
                {
                    string url = target["url"] != null ? target["url"].AsString() : "";
                    string ws = target["webSocketDebuggerUrl"] != null ? target["webSocketDebuggerUrl"].AsString() : "";
                    if (url == "app://-/index.html" && ws.Length > 0) return ws;
                }
            }
            catch { }
        }
        throw new Exception("未找到 Codex 调试目标");
    }

    // ------------------------------------------------------------- args build --

    List<string> BuildArgs()
    {
        var a = new List<string>();
        string wp = _wallpaper.Text.Trim();
        if (wp.Length > 0) { a.Add("--wallpaper"); a.Add(wp); }

        a.Add("--mode"); a.Add(_mode);
        if (_mode == "composite" || _mode == "alpha" || _mode == "adaptive") { a.Add("--alpha"); a.Add(((int)_alpha.Value).ToString()); }
        if (_mode == "overlay") { a.Add("--film"); a.Add(((int)_film.Value).ToString()); }
        else if ((int)_wallAlpha.Value != 255) { a.Add("--wall-alpha"); a.Add(((int)_wallAlpha.Value).ToString()); }
        if (_mode == "adaptive")
        {
            a.Add("--side-alpha"); a.Add(((int)_sideAlpha.Value).ToString());
            a.Add("--input-alpha"); a.Add(((int)_inputAlpha.Value).ToString());
            a.Add("--mask-color"); a.Add(_maskColor);
        }

        string we = _we.Text.Trim();
        if (we.Length > 0) { a.Add("--we"); a.Add(we); }

        string ww = _weWindow.Text.Trim();
        if (ww.Length > 0 && ww != "CodexWallpaperHost") { a.Add("--we-window"); a.Add(ww); }

        string cc = _contentClass.Text.Trim();
        if (cc.Length > 0) { a.Add("--content-class"); a.Add(cc); }

        int round = ParseInt(_round.Text, 0);
        if (round > 0) { a.Add("--round"); a.Add(round.ToString()); }
        int fps = ParseInt(_fps.Text, 30);
        if (fps != 30) { a.Add("--fps"); a.Add(fps.ToString()); }

        if (_full.IsChecked == true) a.Add("--full");
        if (_keepWe.IsChecked == true) a.Add("--keep-we");
        if (_noFallback.IsChecked == true) a.Add("--no-fallback");

        var sel = _target != null ? _target.SelectedItem as WinItem : null;
        if (sel != null && sel.Pid != 0) { a.Add("--pid"); a.Add(sel.Pid.ToString()); }

        a.Add("-v");
        return a;
    }

    void UpdateCommandPreview()
    {
        if (_cmdPreview == null) return;
        _cmdPreview.Text = "we-codex-bg.exe " + JoinArgs(BuildArgs());
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
                                          _target, _full, _keepWe, _noFallback })
            if (c != null) c.IsEnabled = !running;
        foreach (var card in _modeCards) card.IsHitTestVisible = !running;

        if (running) SetStatus("运行中  ·  " + _mode, GreenC);
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
            sb.AppendLine("cfgver=4");
            sb.AppendLine("mode=" + _mode);
            sb.AppendLine("alpha=" + (int)_alpha.Value);
            sb.AppendLine("film=" + (int)_film.Value);
            sb.AppendLine("wallAlpha=" + (int)_wallAlpha.Value);
            sb.AppendLine("sideAlpha=" + (int)_sideAlpha.Value);
            sb.AppendLine("inputAlpha=" + (int)_inputAlpha.Value);
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
            if (map.TryGetValue("sideAlpha", out v)) _sideAlpha.Value = ParseInt(v, 220);
            if (map.TryGetValue("inputAlpha", out v)) _inputAlpha.Value = ParseInt(v, 245);
            if (map.TryGetValue("full", out v)) _full.IsChecked = v == "True";
            if (map.TryGetValue("keepWe", out v)) _keepWe.IsChecked = v == "True";
            if (map.TryGetValue("noFallback", out v)) _noFallback.IsChecked = v == "True";
        }
        catch { }
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        Interlocked.Increment(ref _toneCheckVersion);
        SaveSettings();
        if (_running && _proc != null)
        {
            // stop synchronously so we never leave the Codex window modified
            try
            {
                var proc = _proc;
                IntPtr msg = FindHelperMsgWindow(proc.Id);
                if (msg != IntPtr.Zero) Native.PostMessage(msg, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!proc.WaitForExit(4000))
                {
                    try { proc.Kill(); } catch { }
                    RunRestoreSilent();
                }
            }
            catch { }
        }
        RemoveAutoDarkMode();
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
