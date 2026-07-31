// Setup.cs -- the Windows installer for we-codex-bg.
//
// No Inno Setup / NSIS on the build machine, and the project's whole point is that
// it builds with nothing but the csc.exe shipped with .NET Framework.  So the
// installer is just another single-file WPF app: the payload rides along as
// embedded resources and is written out on install.
//
// Per-user install (%LOCALAPPDATA%\Programs\we-codex-bg) - no admin rights, no UAC.
//
//   we-codex-bg-setup.exe              interactive
//   we-codex-bg-setup.exe /S           silent install
//   we-codex-bg-setup.exe /uninstall   remove
//
// Build: build-setup.bat  ->  dist\we-codex-bg-setup.exe

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using IOPath = System.IO.Path;

internal static class SetupProgram
{
    public const string AppName = "we-codex-bg";
    public const string DisplayName = "WE · Codex 背景";
    public const string Version = "1.0.0";
    public const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\we-codex-bg";

    // resource name -> file name on disk
    public static readonly string[] Payload =
    {
        "we-codex-bg.exe",
        "we-codex-bg-ui.exe",
        "README.md",
    };

    public static string DefaultDir
    {
        get
        {
            return IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", AppName);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = false, uninstall = false;
        foreach (string a in args)
        {
            string s = a.TrimStart('-', '/').ToLowerInvariant();
            if (s == "s" || s == "silent") silent = true;
            else if (s == "uninstall" || s == "u") uninstall = true;
        }

        if (uninstall)
        {
            string dir = InstalledDir() ?? DefaultDir;
            if (silent) { Uninstall(dir); return 0; }
            var r = MessageBox.Show("确定要卸载「" + DisplayName + "」吗？\n\n" + dir,
                                    "卸载", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return 1;
            try { Uninstall(dir); MessageBox.Show("已卸载。", DisplayName); }
            catch (Exception ex) { MessageBox.Show("卸载失败：" + ex.Message, DisplayName); return 2; }
            return 0;
        }

        if (silent)
        {
            try { Install(DefaultDir, true, false); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        }

        var app = new Application();
        return app.Run(new SetupWindow());
    }

    public static string InstalledDir()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKey))
                if (k != null) return k.GetValue("InstallLocation") as string;
        }
        catch { }
        return null;
    }

    // ---- install ----

    public static void Install(string dir, bool startMenu, bool desktop)
    {
        Directory.CreateDirectory(dir);

        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in Payload)
        {
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("安装包内缺少文件：" + name);
                string dest = IOPath.Combine(dir, name);
                // a running instance would lock the exe - stop it first
                TryStopRunning(dest);
                using (var f = File.Create(dest)) s.CopyTo(f);
            }
        }

        // the uninstaller is this very exe, copied next to the payload
        string unins = IOPath.Combine(dir, "uninstall.exe");
        try { TryStopRunning(unins); File.Copy(asm.Location, unins, true); } catch { }

        string uiExe = IOPath.Combine(dir, "we-codex-bg-ui.exe");
        if (startMenu)
        {
            string progs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            CreateShortcut(IOPath.Combine(progs, DisplayName + ".lnk"), uiExe, dir);
        }
        if (desktop)
        {
            string dt = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(IOPath.Combine(dt, DisplayName + ".lnk"), uiExe, dir);
        }

        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey))
        {
            if (k == null) return;
            k.SetValue("DisplayName", DisplayName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "we-codex-bg");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", uiExe);
            k.SetValue("UninstallString", "\"" + unins + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + unins + "\" /uninstall /S");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", DirSizeKb(dir), RegistryValueKind.DWord);
        }
    }

    public static void Uninstall(string dir)
    {
        // stop anything we installed before deleting it
        foreach (string name in Payload) TryStopRunning(IOPath.Combine(dir, name));

        string progs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string dt = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        SafeDelete(IOPath.Combine(progs, DisplayName + ".lnk"));
        SafeDelete(IOPath.Combine(dt, DisplayName + ".lnk"));

        foreach (string name in Payload) SafeDelete(IOPath.Combine(dir, name));

        try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch { }

        // uninstall.exe is running right now, so hand the last step to cmd
        string unins = IOPath.Combine(dir, "uninstall.exe");
        if (File.Exists(unins))
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + unins + "\" & rmdir \"" + dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false };
                Process.Start(psi);
            }
            catch { }
        }
        else { try { Directory.Delete(dir, true); } catch { } }
    }

    static void SafeDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    static void TryStopRunning(string exePath)
    {
        if (!File.Exists(exePath)) return;
        string want = NormalizePath(exePath);
        List<Process> hits = new List<Process>();
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                if (SamePath(p.MainModule.FileName, want)) hits.Add(p);
            }
            catch { }
        }
        if (hits.Count == 0) return;

        foreach (Process p in hits) TryCloseProcess(p);
        WaitForExit(hits, 4000);

        if (string.Equals(IOPath.GetFileName(want), "we-codex-bg.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(want, "--restore")
                { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
            WaitForExit(hits, 2000);
        }

        foreach (Process p in hits)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
        WaitForExit(hits, 2000);
    }

    static string NormalizePath(string path)
    {
        try { return IOPath.GetFullPath(path).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar); }
        catch { return (path ?? "").TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar); }
    }

    static bool SamePath(string a, string b)
    {
        return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    }

    static void TryCloseProcess(Process p)
    {
        try { p.CloseMainWindow(); } catch { }
        try
        {
            foreach (ProcessThread t in p.Threads)
            {
                EnumThreadWindows((uint)t.Id, (h, lp) =>
                {
                    IntPtr res;
                    SendMessageTimeout(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 150, out res);
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch { }
    }

    static void WaitForExit(List<Process> ps, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool any = false;
            foreach (Process p in ps)
            {
                try
                {
                    if (!p.HasExited) { any = true; break; }
                }
                catch { }
            }
            if (!any) return;
            System.Threading.Thread.Sleep(100);
        }
    }

    static int DirSizeKb(string dir)
    {
        long n = 0;
        try { foreach (string f in Directory.GetFiles(dir)) n += new FileInfo(f).Length; }
        catch { }
        return (int)(n / 1024);
    }

    // WScript.Shell via late binding: no COM reference, so plain csc can build it.
    static void CreateShortcut(string lnkPath, string target, string workDir)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                       new object[] { lnkPath });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                            new object[] { "Wallpaper Engine 动态壁纸作为 Codex/ChatGPT 背景" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        catch { }
    }

    const uint WM_CLOSE = 0x0010;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    delegate bool EnumThreadWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
                                          uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}

// ------------------------------------------------------------------ installer UI --

internal sealed class SetupWindow : Window
{
    static readonly Brush Bg = B("#0F1116"), Panel = B("#171A22"), Panel2 = B("#1D212B");
    static readonly Brush Stroke = B("#2A303C"), Text = B("#E7EAF0");
    static readonly Brush Muted = B("#8B93A7"), Faint = B("#5C637A");
    static readonly Brush Accent = B("#5B8CFF"), AccentHi = B("#6E9BFF");

    TextBox _dir;
    CheckBox _startMenu, _desktop, _launch;
    TextBlock _status;
    Button _go;

    public SetupWindow()
    {
        Title = SetupProgram.DisplayName + " 安装程序";
        Width = 560; Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;
        Foreground = Text;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(26) };

        root.Children.Add(new TextBlock
        {
            Text = SetupProgram.DisplayName,
            FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Text
        });
        root.Children.Add(new TextBlock
        {
            Text = "让 Wallpaper Engine 动态壁纸显示在 Codex / ChatGPT 窗口背后 · v" + SetupProgram.Version,
            FontSize = 11.5, Foreground = Faint, Margin = new Thickness(0, 2, 0, 18)
        });

        root.Children.Add(new TextBlock { Text = "安装位置", Foreground = Text, FontSize = 12.5 });
        root.Children.Add(new TextBlock
        {
            Text = "免管理员权限，仅为当前用户安装",
            Foreground = Faint, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 5)
        });
        _dir = new TextBox
        {
            Text = SetupProgram.InstalledDir() ?? SetupProgram.DefaultDir,
            Height = 36, Background = Panel2, Foreground = Text, CaretBrush = Text,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        root.Children.Add(_dir);

        _startMenu = Check("创建开始菜单快捷方式", true);
        _desktop = Check("创建桌面快捷方式", false);
        _launch = Check("安装完成后启动", true);
        var opts = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        opts.Children.Add(_startMenu);
        opts.Children.Add(_desktop);
        opts.Children.Add(_launch);
        root.Children.Add(opts);

        var note = new Border
        {
            Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8), Background = Panel,
            BorderBrush = Stroke, BorderThickness = new Thickness(1)
        };
        note.Child = new TextBlock
        {
            Text = "需要已安装 Steam 版 Wallpaper Engine。程序本身零依赖，只用系统自带的 .NET Framework 4.x。",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(note);

        _status = new TextBlock { Foreground = Faint, FontSize = 11.5, Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(_status);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var cancel = Btn("取消", false);
        cancel.Click += (s, e) => Close();
        row.Children.Add(cancel);
        _go = Btn(SetupProgram.InstalledDir() != null ? "更新" : "安装", true);
        _go.Click += (s, e) => DoInstall();
        row.Children.Add(_go);
        root.Children.Add(row);

        Content = root;
    }

    void DoInstall()
    {
        _go.IsEnabled = false;
        _status.Text = "正在安装…";
        _status.Foreground = Muted;
        try
        {
            string dir = _dir.Text.Trim();
            if (dir.Length == 0) dir = SetupProgram.DefaultDir;
            SetupProgram.Install(dir, _startMenu.IsChecked == true, _desktop.IsChecked == true);
            _status.Text = "安装完成 → " + dir;
            _status.Foreground = B("#3FB950");
            if (_launch.IsChecked == true)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(IOPath.Combine(dir, "we-codex-bg-ui.exe"))
                    { UseShellExecute = true, WorkingDirectory = dir });
                }
                catch { }
            }
            _go.Content = "完成";
            _go.IsEnabled = true;
            _go.Click -= (s, e) => DoInstall();
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败：" + ex.Message;
            _status.Foreground = B("#F76D6D");
            _go.IsEnabled = true;
        }
    }

    CheckBox Check(string text, bool on)
    {
        return new CheckBox
        {
            Content = new TextBlock { Text = text, Foreground = Text, FontSize = 12.5 },
            IsChecked = on, Margin = new Thickness(0, 0, 0, 9), Cursor = Cursors.Hand
        };
    }

    Button Btn(string text, bool primary)
    {
        var b = new Button
        {
            Content = text, Width = 104, Height = 38, Margin = new Thickness(8, 0, 0, 0),
            Foreground = primary ? Brushes.White : Text,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = Cursors.Hand
        };
        var t = new ControlTemplate(typeof(ButtonBase));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, primary ? Accent : Panel2);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.BorderBrushProperty, primary ? Accent : Stroke);
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, primary ? AccentHi : B("#252B37"), "bd"));
        t.Triggers.Add(over);
        var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "bd"));
        t.Triggers.Add(dis);
        b.Template = t;
        return b;
    }

    static SolidColorBrush B(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
