// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SapphireGameHud;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Typeface Font = new("Segoe UI Variable Text");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0) args = [Path.Combine(AppContext.BaseDirectory, "captures", "Next Island.png")];
        if (args[0] != "--interactive") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/NextIsland;component/IslandTheme.xaml", UriKind.Relative)
        });
        var hud = new HudWindow(false);
        if (args[0] == "--interactive")
        {
            hud.Show();
            Field<DispatcherTimer>(hud, "timer").Stop();
            Field<DispatcherTimer>(hud, "hoverTimer").Stop();
            var demoPanel = Field<object>(hud, "panel");
            ((IDisposable)Field<object>(demoPanel, "media")).Dispose();
            var track = NewTrack();
            demoPanel.GetType().GetMethod("UpdateMedia", Private)!.Invoke(demoPanel, [track]);
            Call(hud, "ReceiveMedia", track, PlaybackChange("Started"));
            // Overlay tool windows intentionally do not appear in task switching.
            // Host the actual control in a normal test window for UI automation.
            var viewport = Field<System.Windows.Controls.Grid>(hud, "PanelViewport");
            viewport.Children.Remove((UIElement)demoPanel);
            hud.Hide();
            var host = new Window
            {
                Title = "Next Island · UI verification", SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = (Brush)app.FindResource("IslandFill"), Content = demoPanel
            };
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            tick.Tick += (_, _) => Call(demoPanel, "Tick");
            tick.Start();
            host.Closed += (_, _) => { tick.Stop(); hud.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
            host.Show();
            var close = new DispatcherTimer { Interval = TimeSpan.FromMinutes(4) };
            close.Tick += (_, _) => { close.Stop(); host.Close(); };
            close.Start();
            Dispatcher.Run();
            return;
        }
        Exception? failure = null;
        hud.Show();
        Field<DispatcherTimer>(hud, "timer").Stop();
        Field<DispatcherTimer>(hud, "hoverTimer").Stop();
        ((IDisposable)Field<object>(Field<object>(hud, "panel"), "media")).Dispose();
        var launch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        launch.Tick += (_, _) =>
        {
            launch.Stop();
            try
            {
                Require(new WindowInteropHelper(hud).Handle != 0 && !hud.IsActive, "Passivity");
                var scene = Field<FrameworkElement>(hud, "Scene");
                var panel = Field<FrameworkElement>(hud, "panel");
                var sample = NewTrack();
                panel.GetType().GetMethod("UpdateMedia", Private)!.Invoke(panel, [sample]);
                VerifyPlaybackMotion(sample);
                var now = Environment.TickCount64;
                Write(hud, "mediaPeekUntil", 0L);
                Write(hud, "timerPeekUntil", 0L);
                Call(hud, "UpdateHoverShape", now, false);
                Require(Shape(hud) == "Rest", "Idle shape");
                Settle(hud, 100, 0, 0);
                Require(IsTransparent(Render(scene)), "Idle must paint zero pixels");
                Call(hud, "UpdateHoverShape", now, true);
                Require(Shape(hud) == "Rest", "Passing over edge should stay hidden");
                Call(hud, "UpdateHoverShape", now + 160, true);
                Require(Shape(hud) == "Pill", "Deliberate edge hover reveals pill");
                Call(hud, "UpdateHoverShape", now + 700, true);
                Require(Shape(hud) == "Pill", "Sustained hover must not open controls");
                Call(hud, "UpdateHoverShape", now + 710, false);
                Require(Shape(hud) == "Pill", "Hover exit grace");
                Call(hud, "UpdateHoverShape", now + 850, false);
                Require(Shape(hud) == "Rest", "Pointer leave retracts pill");

                Call(hud, "ReceiveMedia", sample, PlaybackChange("Started"));
                var expiry = Field<long>(hud, "mediaPeekUntil");
                Require(expiry > now, "New track schedules a peek");
                Call(hud, "UpdateHoverShape", expiry - 1, false);
                Require(Shape(hud) == "Pill", "Media peek");
                Call(hud, "UpdateHoverShape", expiry + 150, false);
                Require(Shape(hud) == "Rest", "Media peek expires");
                Call(hud, "ReceiveMedia", sample, PlaybackChange());
                Require(Field<long>(hud, "mediaPeekUntil") == expiry, "Repeated updates should never extend the peek");

                Write(panel, "timerEndsAt", Environment.TickCount64 - 1);
                Call(hud, "Tick");
                var timerExpiry = Field<long>(hud, "timerPeekUntil");
                Require(timerExpiry > now, "Timer completion schedules a peek");
                Call(hud, "UpdateHoverShape", timerExpiry - 1, false);
                Require(Shape(hud) == "Pill", "Timer peek");
                Require(Field<System.Windows.Controls.TextBlock>(hud, "CompactTitle").Text == "Time’s up", "Timer label");
                Call(hud, "UpdateHoverShape", timerExpiry + 150, false);
                Require(Shape(hud) == "Rest", "Timer peek expires");
                Write(panel, "timerCompleted", false);
                Write(hud, "timerPeekUntil", 0L);
                Call(hud, "RefreshCompact", now);

                var emergence = new[] { (100d, 0d, 0d), (123d, 10d, .30), (156d, 22d, .67), (176d, 38d, 1d) };
                var strip = new BitmapSource[emergence.Length];
                for (var i = 0; i < emergence.Length; i++)
                {
                    var (w, h, e) = emergence[i];
                    Settle(hud, w, h, e);
                    strip[i] = CropCenter(Render(scene), 210, 56);
                }

                Call(hud, "TogglePanel");
                Require(Shape(hud) == "Panel", "Explicit action opens controls");
                var panelHeight = (double)panel.GetType().GetProperty("DesiredPanelHeight", Private)!.GetValue(panel)!;
                Require(panelHeight is >= 110 and <= 284, "Music panel height");
                Settle(hud, ((FrameworkElement)panel).Width, panelHeight, 1);
                var controls = CropCenter(Render(scene), 406, (int)Math.Ceiling(panelHeight + 16));
                CaptureDesktopTools(hud, panel, scene, args[0]);
                Call(hud, "DismissPanel");
                Require(Shape(hud) == "Rest", "Explicit dismissal");

                var mode = typeof(HudWindow).GetNestedType("DisplayMode", BindingFlags.NonPublic)!;
                Call(hud, "SetMode", Enum.Parse(mode, "Preview"));
                Require(Shape(hud) == "Game", "Game preview widens");
                Require((GetWindowLong(new WindowInteropHelper(hud).Handle, -20) & 0x20) != 0, "Game HUD must be click-through");
                var layout = typeof(HudWindow).GetMethod("GameWidthFor", BindingFlags.Static | BindingFlags.NonPublic)!;
                var tests = new[] { (1366d, 560d), (1920d, 768d), (2560d, 960d), (3440d, 960d), (3840d / 1.5, 960d) };
                foreach (var (displayWidth, expectedWidth) in tests)
                    Require((double)layout.Invoke(null, [displayWidth])! == expectedWidth, $"HUD width at {displayWidth}");
                var games = Field<object>(hud, "games");
                games.GetType().GetProperty("ShowGraph", Private)!.SetValue(games, false);
                games.GetType().GetProperty("LargeText", Private)!.SetValue(games, false);
                Call(hud, "Position");

                var gameWidth = Field<double>(hud, "gameWidth");
                Settle(hud, gameWidth, 26, 1, 1);
                var bounds = Field<Rect>(hud, "visibleBounds");
                Require(bounds.Top == 7 && bounds.Height == 26, "Game island must float and stay shallow");
                var game = CropCenter(Render(scene), (int)gameWidth + 8, 36);
                VerifyRamReading(hud);
                VerifyTelemetryFit(hud, 600, 13, 26);
                VerifyTelemetryFit(hud, 690, 16, 32);
                Call(hud, "Position");
                var emptyMedia = sample.GetType().GetProperty("Empty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                Call(hud, "ReceiveMedia", emptyMedia, PlaybackChange("Stopped"));
                Require(!Field<bool>(hud, "showGameTrack"), "No game music segment without a track");
                Call(hud, "ReceiveMedia", sample, PlaybackChange("Started"));
                Require(Field<bool>(hud, "showGameTrack"), "Music segment appears during game mode");
                Call(hud, "Tick");
                var spring = typeof(HudWindow).GetNestedType("Spring", BindingFlags.NonPublic)!;
                Write(hud, "musicProgress", Activator.CreateInstance(spring, [1d])!);
                Settle(hud, gameWidth + 154, 26, 1, 1);
                var gameWithMusic = CropCenter(Render(scene), (int)gameWidth + 162, 36);
                Require(Field<Rect>(hud, "visibleBounds").Width == gameWidth + 154, "Game island expands for a track");
                Call(hud, "TogglePanel");
                Require(Shape(hud) == "GameMusic", "Game mode expands into music controls");
                Call(hud, "Tick");
                var gamePanelHeight = (double)panel.GetType().GetProperty("DesiredPanelHeight", Private)!.GetValue(panel)!;
                Settle(hud, gameWidth + 154, 26 + gamePanelHeight, 1, 1);
                Call(Field<object>(hud, "GamePlayback"), "UpdateBars");
                var gameControls = CropCenter(Render(scene), (int)gameWidth + 162, (int)gamePanelHeight + 40);
                Call(hud, "DismissPanel");
                Require(Shape(hud) == "Game", "Game music closes back to live HUD");
                VerifyGameLayouts(hud, scene, games, args[0]);
                VerifyInterruptedMorph(hud, scene);
                VerifyRefinements(hud, panel, sample);
                VerifyRepeatedMotion(scene);
                Save(args[0], strip, controls, game, gameWithMusic, gameControls);
                Console.WriteLine($"PASS: rest, hover intent, compact hover, explicit panel, alerts, click-through, widths at 768p through 4K, longest readings, music panel {panelHeight:0} DIPs");
            }
            catch (Exception e) { failure = e; }
            finally { hud.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        };
        launch.Start();
        Dispatcher.Run();
        if (failure is not null) throw failure;
    }

    private static void VerifyRepeatedMotion(FrameworkElement scene)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var parent = (System.Windows.Controls.Grid)scene;
        var item = new System.Windows.Controls.Border { Width = 32, Height = 32, Background = Brushes.White };
        parent.Children.Add(item);
        parent.UpdateLayout();
        var method = typeof(HudWindow).Assembly.GetType("SapphireGameHud.PlaybackMotion")!
            .GetMethod("Arrive", BindingFlags.Static | BindingFlags.NonPublic)!;
        void Arrive(double offset) => method.Invoke(null, [item, offset, 190, .5]);
        void Wait(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        try
        {
            Arrive(9);
            Wait(50);
            Require(item.Opacity < 1, "First arrival animates");
            Wait(250);
            Arrive(-9);
            Wait(50);
            Require(item.Opacity < 1 && ((TranslateTransform)item.RenderTransform).X < 0,
                "A completed arrival must not disable the next track animation");
            var opacity = item.Opacity;
            var x = ((TranslateTransform)item.RenderTransform).X;
            Arrive(9);
            Require(Math.Abs(item.Opacity - opacity) < .05 && Math.Abs(((TranslateTransform)item.RenderTransform).X - x) < .5,
                "Interrupted arrival keeps its visible position and opacity");
            Wait(250);
            Require(item.Opacity == 1 && ((TranslateTransform)item.RenderTransform).X == 0, "Repeated arrival settles");
        }
        finally { parent.Children.Remove(item); }
        Console.WriteLine("PASS: repeat track transitions, both directions, interruption continuity, and final opacity");
    }

    private static void VerifyRefinements(HudWindow hud, object panel, object sample)
    {
        Call(panel, "SetGameMusicMode", false);
        var viewType = panel.GetType().GetNestedType("View", BindingFlags.NonPublic)!;
        var navigation = Field<FrameworkElement>(panel, "Navigation");
        var state = Field<System.Windows.Controls.TextBlock>(panel, "TimerState");
        var button = Field<System.Windows.Controls.Button>(panel, "TimerButton");
        Call(panel, "ResetTimer", panel, new RoutedEventArgs());
        Require(state.Text == "Ready", "Timer reset state");
        Call(panel, "ToggleTimer", panel, new RoutedEventArgs());
        Require(state.Text == "Counting down" && button.Content.ToString() == "Pause timer", "Timer running state");
        Write(panel, "timerEndsAt", Environment.TickCount64 + 350_000);
        Call(panel, "ToggleTimer", panel, new RoutedEventArgs());
        Require(state.Text == "Paused" && button.Content.ToString() == "Resume timer", "Paused timer can resume");
        var progress = Field<System.Windows.Controls.ProgressBar>(panel, "TimerProgress");
        Require(progress.Value > 0 && progress.Value < 1, "Timer progress tracks remaining time");
        Call(panel, "ResetTimer", panel, new RoutedEventArgs());
        Require(!Field<System.Windows.Controls.Button>(panel, "TimerReset").IsEnabled, "Reset is disabled when already reset");
        var sampleCopy = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(sample, null)!;
        sampleCopy.GetType().GetProperty("Title")!.SetValue(sampleCopy, new string('W', 200));
        sampleCopy.GetType().GetProperty("Artist")!.SetValue(sampleCopy, new string('M', 200));
        panel.GetType().GetMethod("UpdateMedia", Private)!.Invoke(panel, [sampleCopy]);
        foreach (var testWidth in new[] { 328d, 384d })
        {
            Call(panel, "ResizeTo", testWidth);
            double? navTop = null;
            foreach (var name in new[] { "Music", "Shelf", "Clipboard", "Timer", "Music", "Timer" })
            {
                Call(panel, "ShowView", Enum.Parse(viewType, name), false);
                var height = (double)panel.GetType().GetProperty("DesiredPanelHeight", Private)!.GetValue(panel)!;
                Require(height <= 288, $"Panel bounds for {name} at {testWidth}");
                Settle(hud, testWidth, height, 1);
                var top = navigation.TransformToAncestor((FrameworkElement)panel).Transform(new Point()).Y;
                navTop ??= top;
                Require(top == navTop, "Navigation stays anchored across views");
            }
            Call(panel, "SetStatus", Field<System.Windows.Controls.TextBlock>(panel, "ShelfStatus"), "File missing. Remove it and add it again.");
            Call(panel, "ShowView", Enum.Parse(viewType, "Shelf"), false);
            Require((double)panel.GetType().GetProperty("DesiredPanelHeight", Private)!.GetValue(panel)! <= 288, "Shelf error stays inside window");
            Call(panel, "SetStatus", Field<System.Windows.Controls.TextBlock>(panel, "ShelfStatus"), null!);
            Call(panel, "SetStatus", Field<System.Windows.Controls.TextBlock>(panel, "ClipboardStatus"), "Clipboard busy. Try copying again.");
            Call(panel, "ShowView", Enum.Parse(viewType, "Clipboard"), false);
            Require((double)panel.GetType().GetProperty("DesiredPanelHeight", Private)!.GetValue(panel)! <= 288, "Clipboard error stays inside window");
            Call(panel, "SetStatus", Field<System.Windows.Controls.TextBlock>(panel, "ClipboardStatus"), null!);
        }
        Call(panel, "ResizeTo", 384d);
        panel.GetType().GetMethod("UpdateMedia", Private)!.Invoke(panel, [sample]);
        Console.WriteLine("PASS: timer start/pause/resume/reset, stable tab positions, long media titles, 328/384 DIP panels, and visible error layouts");
    }

    private static void VerifyInterruptedMorph(HudWindow hud, FrameworkElement scene)
    {
        var kind = typeof(HudWindow).GetNestedType("IslandShape", BindingFlags.NonPublic)!;
        void Set(string name) => Call(hud, "SetShape", Enum.Parse(kind, name));
        void Wait(int ms)
        {
            var frame = new DispatcherFrame();
            var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            once.Tick += (_, _) => { once.Stop(); frame.Continue = false; };
            once.Start();
            Dispatcher.PushFrame(frame);
        }
        Set("Rest");
        Wait(480);
        Set("Pill");
        Wait(75);
        Set("Rest");
        Wait(48);
        Set("Panel");
        Wait(85);
        Set("Rest");
        Wait(500);
        Require(!Field<bool>(hud, "animatingMorph"), "Interrupted transition must settle");
        Require(IsTransparent(Render(scene)), "Interrupted transition must leave no residue");

        var bars = Field<object>(hud, "CompactPlayback");
        var barTimer = Field<DispatcherTimer>(bars, "timer");
        Set("Pill");
        Wait(500);
        Call(bars, "SetPlaying", true);
        if (SystemParameters.ClientAreaAnimation)
            Require(barTimer.IsEnabled, "Visible playback bars animate");
        Call(bars, "SetPlaying", false);
        Wait(500);
        Require(!barTimer.IsEnabled, "Paused bars stop scheduling redraws");
        Set("Rest");
        Wait(480);
        Require(!barTimer.IsEnabled, "Hidden bars stay stopped");
        Console.WriteLine("PASS: interrupted morph settles, idle clears, paused and hidden playback bars stop");
    }

    private static void VerifyGameLayouts(HudWindow hud, FrameworkElement scene, object games, string destination)
    {
        var displayCases = new[] { ("768p", 1366d), ("1080p", 1920d), ("1440p", 2560d),
            ("Ultrawide", 3440d), ("4K at 150%", 2560d), ("Narrow with large text", 490d) };
        var visual = new DrawingVisual();
        var large = games.GetType().GetProperty("LargeText", Private)!;
        var spring = typeof(HudWindow).GetNestedType("Spring", BindingFlags.NonPublic)!;
        var gameTitle = Field<System.Windows.Controls.TextBlock>(hud, "GameTrackTitle");
        gameTitle.Text = "A deliberately long track title to check clipping";
        using (var drawing = visual.RenderOpen())
        {

            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(31, 33, 42)), null, new Rect(0, 0, 1180, 540));
            for (var i = 0; i < displayCases.Length; i++)
            {
                var (label, available) = displayCases[i];
                large.SetValue(games, i == displayCases.Length - 1);
                Call(hud, "ApplyDisplayWidth", available);
                var width = Field<double>(hud, "gameWidth");
                var pocket = Field<double>(hud, "musicSlotWidth");
                var height = (double)typeof(HudWindow).GetProperty("GameHeight", Private)!.GetValue(hud)!;
                Write(hud, "musicProgress", Activator.CreateInstance(spring, [1d])!);
                Settle(hud, width + pocket, height, 1, 1);
                VerifyMetricBounds(hud);
                var outline = Field<Geometry>(hud, "visibleOutline");
                Require(outline.Bounds.Width <= available && height <= 64, "Game layout footprint " + label);
                var clip = CropCenter(Render(scene), (int)Math.Ceiling(width + pocket + 8), (int)Math.Ceiling(height + 14));
                drawing.DrawImage(clip, new Rect((1180 - clip.PixelWidth) / 2d, i * 82 + 27, clip.PixelWidth, clip.PixelHeight));
                Text(drawing, label, 12, 24, i * 82 + 10, Brushes.White);
            }
        }
        var result = new RenderTargetBitmap(1180, 540, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var output = File.Create(Path.ChangeExtension(destination, ".displays.png"));
        encoder.Save(output);
        Console.WriteLine("PASS: display layout fixtures, optional music width, and narrow large-text wrap");
    }

    private static void CaptureDesktopTools(HudWindow hud, object panel, FrameworkElement scene, string destination)
    {
        var type = panel.GetType().GetNestedType("View", BindingFlags.NonPublic)!;
        var names = new[] { "Shelf", "Clipboard", "Timer" };
        var fixtureFolder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "sample-shelf");
        Directory.CreateDirectory(fixtureFolder);
        var notes = Path.Combine(fixtureFolder, "Design notes.md");
        var checklist = Path.Combine(fixtureFolder, "Release checklist.md");
        if (!File.Exists(notes)) File.WriteAllText(notes, "Sample file for the Next Island visual check.");
        if (!File.Exists(checklist)) File.WriteAllText(checklist, "Sample file for the Next Island visual check.");
        Call(panel, "AddPaths", (object)new[] { notes, checklist });
        Call(panel, "ResetTimer", panel, new RoutedEventArgs());
        Write(panel, "timerEndsAt", Environment.TickCount64 + 1_122_000);
        Call(panel, "UpdateTimer");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(39, 42, 51)), null, new Rect(0, 0, 1260, 340));
            for (var i = 0; i < names.Length; i++)
            {
                Call(panel, "ShowView", Enum.Parse(type, names[i]), false);
                var viewHeight = (double)panel.GetType().GetProperty("DesiredPanelHeight", Private)!.GetValue(panel)!;
                var width = ((FrameworkElement)panel).Width;
                Settle(hud, width, viewHeight, 1);
                viewHeight = (double)panel.GetType().GetProperty("DesiredPanelHeight", Private)!.GetValue(panel)!;
                Settle(hud, width, viewHeight, 1);
                var body = Field<FrameworkElement>(panel, "PanelBody");
                Require(body.ActualHeight + body.Margin.Top + body.Margin.Bottom <= ((FrameworkElement)panel).ActualHeight + 1,
                    $"Navigation must not clip the actions in {names[i]} (requested {viewHeight}, panel {((FrameworkElement)panel).ActualHeight}, body {body.ActualHeight}, margin {body.Margin})");
                var bitmap = CropCenter(Render(scene), (int)Math.Ceiling(width + 12), (int)Math.Ceiling(viewHeight + 16));
                var x = i * 420 + (420 - bitmap.PixelWidth) / 2;
                drawing.DrawImage(bitmap, new Rect(x, 39, bitmap.PixelWidth, bitmap.PixelHeight));
                Text(drawing, names[i], 14, i * 420 + 27, 13, Brushes.White);
            }
        }
        Call(panel, "ShowView", Enum.Parse(type, "Music"), false);
        Call(panel, "ResetTimer", panel, new RoutedEventArgs());
        var image = new RenderTargetBitmap(1260, 340, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.ChangeExtension(destination, ".tools.png"));
        png.Save(output);
    }

    private static object PlaybackChange(string name = "None") =>
        Enum.Parse(typeof(HudWindow).Assembly.GetType("SapphireGameHud.PlaybackChange")!, name);

    private static void VerifyPlaybackMotion(object sample)
    {
        var type = sample.GetType();
        var assembly = type.Assembly;
        var direction = assembly.GetType("SapphireGameHud.TrackDirection")!;
        var observe = assembly.GetType("SapphireGameHud.PlaybackMotion")!.GetMethod("Observe",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object Change(object before, object after, string dir = "None", bool seeking = false, double seconds = .1) =>
            observe.Invoke(null, [before, after, seconds, Enum.Parse(direction, dir), seeking])!;
        object Read(params (string Name, object Value)[] values)
        {
            var copy = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(sample, null)!;
            foreach (var (name, value) in values)
                type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(copy, value);
            return copy;
        }
        void Check(string name, object before, object after, string dir = "None", bool seeking = false, double seconds = .1) =>
            Require(Change(before, after, dir, seeking, seconds).ToString() == name, "Playback case: " + name);
        var paused = Read(("Playing", false));
        var next = Read(("Title", "Another track"), ("Position", 0d));
        var ending = Read(("Position", 227d));
        var stopped = Read(("Position", 0d), ("Playing", false), ("Stopped", true));
        var restart = Read(("Position", 0d));
        Check("None", sample, Read(("Position", 73d)));
        Check("Paused", sample, paused);
        Check("Resumed", paused, sample);
        Check("Next", sample, next, "Forward");
        Check("Previous", sample, next, "Backward");
        Check("Restarted", sample, restart, "Backward");
        Check("None", sample, restart, "Backward", seeking: true);
        Check("None", sample, restart, "Forward");
        Check("Advanced", ending, next);
        Check("Advanced", ending, restart);
        Check("Ended", ending, stopped);
        Check("Stopped", sample, stopped);
        Check("Changed", ending, Read(("Title", "Another track"), ("Source", "Different player")));
        Console.WriteLine("PASS: 13 playback event cases including pause, resume, both skip directions, seek, repeat, natural end, and session switch");
    }

    private static void Settle(HudWindow hud, double width, double height, double reveal, double attached = 0)
    {
        Call(hud, "StopMorph");
        var spring = typeof(HudWindow).GetNestedType("Spring", BindingFlags.NonPublic)!;
        Write(hud, "gameProgress", Activator.CreateInstance(spring, [attached])!);
        Call(hud, "DrawMorph", width, height, reveal);
        hud.UpdateLayout();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    private static BitmapSource CropCenter(BitmapSource image, int width, int height) =>
        new CroppedBitmap(image, new Int32Rect((image.PixelWidth - width) / 2, 0, width, height));

    private static void VerifyTelemetryFit(HudWindow hud, double width, double fontSize, double height)
    {
        var telemetry = Field<System.Windows.Controls.Border>(hud, "TelemetryContent");
        Write(hud, "gameWidth", width);
        telemetry.Width = width;
        telemetry.Height = height;
        System.Windows.Documents.TextElement.SetFontSize(telemetry, fontSize);
        Field<System.Windows.Controls.TextBlock>(hud, "Fps").Text = "9999";
        Field<System.Windows.Documents.Run>(hud, "FrameTimeNumber").Text = "999.9";
        foreach (var name in new[] { "Cpu", "Gpu", "Ram" }) Field<System.Windows.Documents.Run>(hud, name + "Number").Text = "100";
        foreach (var name in new[] { "CpuTemp", "GpuTemp" }) Field<System.Windows.Documents.Run>(hud, name + "Number").Text = "100";
        Settle(hud, width, height, 1, 1);
        VerifyMetricBounds(hud);
    }

    private static void VerifyRamReading(HudWindow hud)
    {
        var telemetry = typeof(HudWindow).Assembly.GetType("SapphireGameHud.HardwareTelemetry")!;
        var percentage = (double?)telemetry.GetMethod("ReadRam", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        Require(percentage is >= 0 and <= 100, "Windows reports physical RAM usage");

        var usage = typeof(HudWindow).Assembly.GetType("SapphireGameHud.UsageReading")!;
        Call(hud, "ShowUsage", Activator.CreateInstance(usage)!);
        Require(Field<System.Windows.Documents.Run>(hud, "RamNumber").Text == "—"
            && Field<System.Windows.Documents.Run>(hud, "RamUnit").Text == "", "Unavailable RAM is shown as a dash");
        Call(hud, "Tick");
        Require(Field<System.Windows.Documents.Run>(hud, "RamNumber").Text == "62"
            && Field<System.Windows.Documents.Run>(hud, "RamUnit").Text == "%", "Preview shows RAM percentage");
        Console.WriteLine($"PASS: Windows reports {percentage:0}% physical RAM usage, with preview and unavailable states");
    }

    private static void VerifyMetricBounds(HudWindow hud)
    {
        var telemetry = Field<System.Windows.Controls.Border>(hud, "TelemetryContent");
        foreach (var name in new[] { "Fps", "FrameTime", "Cpu", "Gpu", "CpuTemp", "GpuTemp", "Ram" })
        {
            var value = Field<System.Windows.Controls.TextBlock>(hud, name);
            var group = (System.Windows.Controls.StackPanel)value.Parent;
            var groupBounds = group.TransformToAncestor(telemetry).TransformBounds(new Rect(group.RenderSize));
            var grid = (System.Windows.Controls.Grid)group.Parent;
            var col = System.Windows.Controls.Grid.GetColumn(group);
            var span = System.Windows.Controls.Grid.GetColumnSpan(group);
            var slotWidth = 0d;
            for (var i = col; i < col + span; i++) slotWidth += grid.ColumnDefinitions[i].ActualWidth;
            Require(group.RenderSize.Width <= slotWidth + .1, $"Column clipped at {telemetry.Width}, {name}");
            Require(groupBounds.Left >= 0 && groupBounds.Right <= telemetry.Width + .1,
                $"Reading out of bounds at {telemetry.Width}, {name}");
        }
    }

    private static string Shape(HudWindow hud) => Field<object>(hud, "shape").ToString()!;
    private static T Field<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field, Private | BindingFlags.Public)!.GetValue(instance)!;
    private static void Write(object instance, string field, object value) =>
        instance.GetType().GetField(field, Private)!.SetValue(instance, value);
    private static void Call(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, Private)!.Invoke(instance, args);
    private static void Require(bool test, string message)
    {
        if (!test) throw new InvalidOperationException(message);
    }

    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        var result = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(element);
        return result;
    }

    private static bool IsTransparent(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) return false;
        return true;
    }

    private static object NewTrack()
    {
        var image = new DrawingVisual();
        using (var g = image.RenderOpen())
        {
            g.DrawRectangle(new LinearGradientBrush(Color.FromRgb(52, 68, 139), Color.FromRgb(210, 145, 158), 67),
                null, new Rect(0, 0, 96, 96));
            g.DrawEllipse(new SolidColorBrush(Color.FromRgb(245, 196, 164)), null, new Point(61, 37), 17, 17);
            for (var i = 0; i < 19; i++)
            {
                var b = new SolidColorBrush(Color.FromArgb((byte)(100 + i * 7), 18, 21, 49));
                g.DrawEllipse(b, null, new Point(16 + 8 * Math.Sin(i * .34), 83 + i * 3.5), 102 - i * 2, 22);
            }
        }
        var art = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        art.Render(image);
        var type = typeof(HudWindow).Assembly.GetType("SapphireGameHud.MediaReading")!;
        return Activator.CreateInstance(type, ["Night Drive", "Sample track", art, true, true, true, true, true, 72d, 228d])!;
    }

    private static void Save(string path, BitmapSource[] phases, BitmapSource panel, BitmapSource game, BitmapSource gameWithMusic, BitmapSource gameControls)
    {
        var visual = new DrawingVisual();
        var edge = new Pen(new SolidColorBrush(Color.FromRgb(115, 115, 126)), 1);
        var stage = new LinearGradientBrush(Color.FromRgb(54, 54, 66), Color.FromRgb(31, 32, 39), 65);
        using (var g = visual.RenderOpen())
        {
            g.DrawRectangle(new SolidColorBrush(Color.FromRgb(21, 21, 25)), null, new Rect(0, 0, 1232, 956));
            Text(g, "Next Island", 25, 32, 20, Brushes.White);
            Text(g, "Hidden at rest. Reveals at the top edge.", 13, 33, 56, Brushes.Silver);
            for (var i = 0; i < phases.Length; i++)
            {
                var x = 32 + i * 295;
                g.DrawRoundedRectangle(stage, edge, new Rect(x, 91, 233, 105), 7, 7);
                g.DrawImage(phases[i], new Rect(x + 11.5, 92, phases[i].PixelWidth, phases[i].PixelHeight));
                Text(g, new[] { "Idle", "Emerging", "Detaching", "Track alert" }[i], 12, x + 14, 171, Brushes.Gainsboro);
            }
            g.DrawRoundedRectangle(stage, edge, new Rect(32, 219, 1168, 278), 9, 9);
            g.DrawImage(panel, new Rect((1232 - panel.PixelWidth) / 2d, 220, panel.PixelWidth, panel.PixelHeight));
            g.DrawRoundedRectangle(stage, edge, new Rect(24, 517, 1184, 60), 9, 9);
            g.DrawImage(game, new Rect((1232 - game.PixelWidth) / 2d, 518, game.PixelWidth, game.PixelHeight));
            g.DrawRoundedRectangle(stage, edge, new Rect(24, 597, 1184, 55), 9, 9);
            g.DrawImage(gameWithMusic, new Rect((1232 - gameWithMusic.PixelWidth) / 2d, 598, gameWithMusic.PixelWidth, gameWithMusic.PixelHeight));
            g.DrawRoundedRectangle(stage, edge, new Rect(24, 664, 1184, 272), 9, 9);
            g.DrawImage(gameControls, new Rect((1232 - gameControls.PixelWidth) / 2d, 665, gameControls.PixelWidth, gameControls.PixelHeight));
        }
        var bitmap = new RenderTargetBitmap(1232, 956, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Text(DrawingContext g, string value, double size, double x, double y, Brush color) =>
        g.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, color, 1),
            new Point(x, y));
}
