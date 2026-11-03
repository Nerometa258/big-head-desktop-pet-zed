using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;
using System.Collections.Generic;
using System.Text;

class PetWindow : Window
{
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    readonly ScaleTransform squash = new(1, 1);
    readonly MediaPlayer press = new(), release = new();
    const int LongPressMilliseconds = 200;
    readonly DispatcherTimer holdTimer = new() { Interval = TimeSpan.FromMilliseconds(LongPressMilliseconds) };
    readonly Stopwatch holdWatch = new();
    bool held, longPressPlayed;
    Point start, origin;
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigHeadDesktopPet", "position.txt");
    public PetWindow()
    {
        Title = "大脑袋桌宠";
        Width = 192; Height = 192;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = Environment.GetCommandLineArgs().Length > 1;
        var image = new Image { Width = 152, Height = 152, Stretch = Stretch.Uniform,
            Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "pet.png"))),
            RenderTransform = squash, RenderTransformOrigin = new Point(.5, .75), Cursor = Cursors.Hand };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var grid = new Grid(); grid.Children.Add(image); Content = grid;
        press.Open(new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "press.mp3")));
        release.Open(new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "release.mp3")));
        holdTimer.Tick += (_, _) => {
            holdTimer.Stop();
            if (held && Mouse.LeftButton == MouseButtonState.Pressed) PlayLongPress();
        };
        var menu = new ContextMenu();
        foreach (var entry in AppLocator.Apps) {
            var item = new MenuItem { Header = "打开" + entry.Name };
            item.Click += (_, _) => AppLauncher.StartWorker(entry.Key);
            menu.Items.Add(item);
        }
        var exit = new MenuItem { Header = "退出桌宠" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit); image.ContextMenu = menu;
        image.MouseLeftButtonDown += (_, e) => {
            held = true; start = CursorPoint(); origin = new Point(Left, Top);
            longPressPlayed = false; press.Stop(); release.Stop();
            image.CaptureMouse(); holdWatch.Restart(); holdTimer.Start();
            Animate(1.17, .78, false); e.Handled = true;
        };
        image.MouseMove += (_, _) => {
            if (!held) return;
            var p = CursorPoint(); Left = origin.X + p.X - start.X; Top = origin.Y + p.Y - start.Y;
        };
        image.MouseLeftButtonUp += (_, e) => { FinishPress(true); image.ReleaseMouseCapture(); e.Handled = true; };
        image.LostMouseCapture += (_, _) => FinishPress(false);
        Deactivated += (_, _) => { if (held && Mouse.LeftButton != MouseButtonState.Pressed) { FinishPress(false); image.ReleaseMouseCapture(); } };
        Loaded += (_, _) => {
            Left = SystemParameters.WorkArea.Right - Width - 30; Top = SystemParameters.WorkArea.Bottom - Height - 30;
            try {
                var parts = File.ReadAllText(settings).Split(',');
                var x = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var y = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                if (x + Width > SystemParameters.VirtualScreenLeft && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && y + Height > SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) { Left = x; Top = y; }
            } catch { }
        };
        Closed += (_, _) => {
            holdTimer.Stop(); holdWatch.Stop();
            press.Close(); release.Close();
            try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); File.WriteAllText(settings, FormattableString.Invariant($"{Left},{Top}")); } catch { }
        };
    }
    Point CursorPoint()
    {
        GetCursorPos(out var p);
        return PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.Transform(new Point(p.X, p.Y)) ?? new Point(p.X, p.Y);
    }
    void PlayLongPress() { if (longPressPlayed) return; longPressPlayed = true; press.Stop(); Play(release); }
    void FinishPress(bool completed)
    {
        if (!held) return;
        holdTimer.Stop(); holdWatch.Stop();
        if (completed && !longPressPlayed) {
            // Short clicks can only be identified once the button is released.
            if (holdWatch.ElapsedMilliseconds < LongPressMilliseconds) Play(press);
            else PlayLongPress();
        }
        held = false; Animate(1, 1, true);
    }
    static void Play(MediaPlayer player) { player.Position = TimeSpan.Zero; player.Play(); }
    void Animate(double x, double y, bool bounce)
    {
        IEasingFunction ease = bounce ? new ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(bounce ? 480 : 140);
        squash.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(x, duration) { EasingFunction = ease });
        squash.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(y, duration) { EasingFunction = ease });
    }
    [STAThread] static void Main(string[] args)
    {
        try {
            if (AppLauncher.HandleCommand(args)) return;
            using var mutex = new Mutex(true, "BigHeadDesktopPet.Singleton", out bool first);
            if (!first) return;
            var app = new Application();
            app.Run(new PetWindow());
        } catch (Exception e) {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "error.log"), e.ToString());
            MessageBox.Show(e.Message, "大脑袋桌宠启动失败");
        }
    }
}
