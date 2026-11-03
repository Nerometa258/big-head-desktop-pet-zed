using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;

static class AppLauncher
{
    record AppInfo(string Key, string[] Processes);
    static readonly AppInfo[] Apps = {
        new("Weixin", new[] { "Weixin", "WeChat" }),
        new("Steam", new[] { "steam", "steamwebhelper" }),
        new("WeGame", new[] { "wegame", "tgp_daemon", "tgp_launcher", "wegame_env" }),
        new("5E", new[] { "5EClient" })
    };
    record AppWindow(long Handle, uint ProcessId, string Class, string Title, bool Visible, bool Iconic, int Width, int Height);
    static readonly string LogPath = Path.Combine(AppLocator.DataDirectory, "launcher.log");
    static void Log(string text) { try { Directory.CreateDirectory(AppLocator.DataDirectory); File.AppendAllText(LogPath, $"{DateTime.Now:O} {text}\n"); } catch { } }
    public static void StartWorker(string key)
    {
        var app = Apps.First(a => a.Key == key);
        try {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--open"); start.ArgumentList.Add(app.Key);
            using var worker = Process.Start(start);
            if (worker != null) AllowSetForegroundWindow((uint)worker.Id);
        } catch (Exception e) { Log("worker failed: " + e.Message); }
    }
    public static bool HandleCommand(string[] args)
    {
        if (args.Length == 0 || (args[0] != "--open" && args[0] != "--inspect" && args[0] != "--detect-apps")) return false;
        if (args[0] == "--detect-apps") {
            var report = Apps.Select(a => new { a.Key, Location = AppLocator.Find(a.Key) }).ToArray();
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { RuntimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(), Applications = report }, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        if (args[0] == "--inspect") {
            var report = Apps.Select(a => new { a.Key, Windows = FindWindows(a) }).ToArray();
            File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        var app = Apps.First(a => a.Key == args[1]);
        using var gate = new Mutex(true, "BigHeadDesktopPet.Launch." + app.Key, out bool first);
        if (!first) return true;
        // Native activation never runs on the pet's UI thread. The helper is bounded.
        using var timeout = new Timer(_ => Environment.Exit(124), null, 120000, Timeout.Infinite);
        try { Open(app); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { Log(app.Key + " UAC cancelled"); }
        catch (Exception e) { Log(app.Key + " failed: " + e); }
        return true;
    }
    static List<AppWindow> FindWindows(AppInfo app)
    {
        var ids = new HashSet<uint>();
        ProcessIdToSessionId((uint)Environment.ProcessId, out uint session);
        foreach (string name in app.Processes)
            foreach (var process in Process.GetProcessesByName(name)) using (process) {
                if (ProcessIdToSessionId((uint)process.Id, out uint other) && other == session) ids.Add((uint)process.Id);
            }
        // Modern launchers host their actual UI in a differently named child process.
        // Follow parent IDs rather than collecting unrelated processes named browser.exe.
        var parents = new List<(uint Id, uint Parent)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot != new IntPtr(-1)) {
            try {
                var entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snapshot, ref entry)) do { parents.Add((entry.Id, entry.Parent)); } while (Process32Next(snapshot, ref entry));
            } finally { CloseHandle(snapshot); }
        }
        bool changed;
        do {
            changed = false;
            foreach (var child in parents)
                if (!ids.Contains(child.Id) && ids.Contains(child.Parent)
                    && ProcessIdToSessionId(child.Id, out uint other) && other == session) changed |= ids.Add(child.Id);
        } while (changed);
        var windows = new List<AppWindow>();
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out uint pid);
            if (!ids.Contains(pid)) return true;
            var title = new StringBuilder(512); var cls = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity); GetClassName(window, cls, cls.Capacity);
            GetWindowRect(window, out var r);
            windows.Add(new(window.ToInt64(), pid, cls.ToString(), title.ToString(), IsWindowVisible(window), IsIconic(window), r.Right-r.Left, r.Bottom-r.Top));
            return true;
        }, IntPtr.Zero);
        return windows;
    }
    static long Score(AppInfo app, AppWindow w)
    {
        if ((w.Width < 200 || w.Height < 100) && !w.Iconic) return -1;
        // Tray/message/helper windows can be as large as the desktop. Never show them.
        bool main = app.Key switch {
            "Weixin" => (w.Class.Contains("QWindowIcon") && (w.Title == "微信" || w.Title == "Weixin" || w.Title == "WeChat"))
                || w.Class == "WeChatMainWndForPC" || w.Class == "WeChatLoginWndForPC",
            "Steam" => w.Class == "SDL_app" && w.Title == "Steam",
            "WeGame" => (w.Class == "TWINCONTROL" || w.Class == "Chrome_WidgetWin_1" || w.Class == "CefTopWindow") && w.Title.Contains("WeGame", StringComparison.OrdinalIgnoreCase),
            "5E" => w.Class == "Chrome_WidgetWin_1" && w.Title == "5E对战平台",
            _ => false
        };
        if (!main) return -1;
        long score = Math.Min((long)w.Width * w.Height, 10000000);
        if (w.Title.Length > 0) score += 20000000;
        if (w.Class == "CefTopWindow") score += 50000000;
        if (w.Visible) score += 10000000;
        return score;
    }
    static void Open(AppInfo app)
    {
        Log(app.Key + " request");
        var windows = FindWindows(app);
        Log(app.Key + " candidate count=" + windows.Count);
        var best = windows.Where(w => Score(app, w) >= 0).OrderByDescending(w => Score(app, w)).FirstOrDefault();
        // Closing Weixin to the tray suspends its own UI state. Showing its HWND
        // directly only exposes the surface; let Weixin reopen itself instead.
        if (app.Key == "Weixin" && ((best != null && !best.Visible) || (best == null && windows.Count > 0))) {
            bool tray = RestoreViaTray(app);
            Log("Weixin closed-to-tray invoke=" + tray);
            bool visible = false;
            if (tray) {
                for (int i = 0; i < 20; i++) {
                    Thread.Sleep(150);
                    visible = FindWindows(app).Any(w => Score(app, w) >= 0 && w.Visible);
                    if (visible) break;
                }
            }
            Log("Weixin closed-to-tray visible=" + visible);
            // Never fall back to ShowWindow/SetWindowPos or a second Weixin process.
            return;
        }
        if (best != null) {
            var hwnd = new IntPtr(best.Handle);
            Log(app.Key + $" restore {best.Handle} {best.Class}");
            if (!best.Visible && (app.Key == "5E" || app.Key == "WeGame")) {
                bool tray = RestoreViaTray(app);
                Log(app.Key + " tray invoke=" + tray);
                if (tray) {
                    for (int i = 0; i < 30 && !IsWindowVisible(hwnd); i++) Thread.Sleep(100);
                }
            }
            bool restore = ShowWindowAsync(hwnd, 9);
            bool show = ShowWindowAsync(hwnd, 5);
            bool position = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x4000 | 0x40 | 0x1 | 0x2);
            bool foreground = SetForegroundWindow(hwnd);
            for (int i = 0; i < 30 && !IsWindowVisible(hwnd); i++) Thread.Sleep(100);
            Log(app.Key + $" restored visible={IsWindowVisible(hwnd)} restore={restore} show={show} position={position} foregroundRequest={foreground} foreground={GetForegroundWindow().ToInt64()}");
            // A known existing UI must never fall through to another elevated launch.
            return;
        }
        string? executable = AppLocator.FindOrChoose(app.Key);
        if (executable == null) { Log(app.Key + " selection cancelled"); return; }
        Log(app.Key + " launch executable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (app.Key == "Steam") start.Arguments = "steam://open/main";
        Process.Start(start)?.Dispose();
    }
    static bool RestoreViaTray(AppInfo app)
    {
        var roots = new List<IntPtr>();
        EnumWindows((window, _) => {
            var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
            if (cls.ToString() is "Shell_TrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") roots.Add(window);
            return true;
        }, IntPtr.Zero);
        AutomationElement? expand = null;
        foreach (var root in roots) {
            try {
                var elements = AutomationElement.FromHandle(root).FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)));
                foreach (AutomationElement item in elements) {
                    string label = item.Current.Name;
                    if (MatchesTrayApp(label, app) && ActivateTrayApp(item, app)) return true;
                    if (label.Contains("隐藏的图标") || label.Contains("hidden icons", StringComparison.OrdinalIgnoreCase)) expand = item;
                }
            } catch (Exception e) { Log("tray scan " + e.Message); }
        }
        if (expand == null || !InvokeTray(expand)) return false;
        Thread.Sleep(350);
        try {
            // The overflow island is created lazily by Explorer.
            var desktop = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement root in desktop) {
                if (root.Current.ClassName is not ("NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")) continue;
                var elements = root.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)));
                foreach (AutomationElement item in elements) {
                    string label = item.Current.Name;
                    if (MatchesTrayApp(label, app) && ActivateTrayApp(item, app)) return true;
                }
            }
        } catch (Exception e) { Log("overflow scan " + e.Message); }
        return false;
    }
    static bool InvokeTray(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)) { ((InvokePattern)pattern).Invoke(); return true; }
        return false;
    }
    static bool MatchesTrayApp(string label, AppInfo app) => app.Key == "Weixin"
        ? label.Trim().StartsWith("微信", StringComparison.OrdinalIgnoreCase)
            || label.Trim().StartsWith("Weixin", StringComparison.OrdinalIgnoreCase)
            || label.Trim().StartsWith("WeChat", StringComparison.OrdinalIgnoreCase)
        : label.Contains(app.Key, StringComparison.OrdinalIgnoreCase);
    static bool ActivateTrayApp(AutomationElement element, AppInfo app)
    {
        if (app.Key != "WeGame" && app.Key != "Weixin") return InvokeTray(element);
        // Open through the app's tray double-click handler, not native HWND display.
        if (element.Current.IsOffscreen) return false;
        if (!element.TryGetClickablePoint(out var point)) {
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
            point = new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
        GetPhysicalCursorPos(out var previous);
        try {
            SetPhysicalCursorPos((int)point.X, (int)point.Y);
            var clicks = new[] { MouseInput(2), MouseInput(4), MouseInput(2), MouseInput(4) };
            return SendInput((uint)clicks.Length, clicks, Marshal.SizeOf<INPUT>()) == clicks.Length;
        } finally { SetPhysicalCursorPos(previous.X, previous.Y); }
    }
    static INPUT MouseInput(uint flags) => new() { Type = 0, Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags } } };
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int X,Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PROCESSENTRY32 {
        public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string File;
    }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
}
