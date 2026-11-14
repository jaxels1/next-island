// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SapphireGameHud;

public partial class HudWindow : Window
{
    private enum DisplayMode { Automatic, Always, Hidden, Preview }
    private enum IslandShape { Rest, Pill, Panel, Game, GameMusic }

    private const double CompactMinWidth = 176;
    private const double CompactMaxWidth = 280;
    private const double MaxGameWidth = 960;
    private const double WindowWidth = 1130;
    private const double WindowHeight = 300;
    private const double PanelWidth = 384;
    private const double PillHeight = 38;
    private const double GameTrackWidth = 154;
    private const double GameGap = 7;
    private const int HoverIntentMs = 150;
    private const int HoverExitMs = 125;

    private static readonly double[] PreviewHistory =
        [12.2, 12.1, 13, 12.5, 12, 20, 14, 12.5, 12.2, 12, 12.6, 13.3, 15, 12.5, 12.4, 12.7];

    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem automatic;
    private readonly Forms.ToolStripMenuItem always;
    private readonly Forms.ToolStripMenuItem hidden;
    private readonly Forms.ToolStripMenuItem preview;
    private readonly Forms.ToolStripMenuItem frameStatus;
    private readonly Forms.ToolStripMenuItem temperatureStatus;
    private readonly Forms.ToolStripMenuItem openIsland;
    private readonly Forms.ToolStripMenuItem gameRule;
    private readonly Forms.ToolStripMenuItem markGame;
    private readonly Forms.ToolStripMenuItem ignoreApp;
    private readonly Forms.ToolStripMenuItem resetRule;
    private readonly Forms.ToolStripMenuItem graphOption;
    private readonly Forms.ToolStripMenuItem largeTextOption;
    private readonly Forms.ToolStripMenuItem sensorSetup;
    private readonly GameDetection games = new();
    private readonly List<Task> closingCaptures = [];
    private IslandPanel? panel;
    private MediaReading currentMedia = MediaReading.Empty;
    private bool clipboardListening;
    private bool panelPinned;
    private bool choosingFile;
    private bool dismissedUntilLeave;
    private long hoverStarted;
    private long leaveStarted;
    private long mediaPeekUntil;
    private string? playbackNotice;
    private long timerPeekUntil;
    private bool timerCompleted;
    private IslandShape shape;
    private Spring width = new(100);
    private Spring height = new(0);
    private Spring emergence = new(0);
    private Spring gameProgress = new(0);
    private Spring musicProgress = new(0);
    private bool showGameTrack;
    private bool gameMusicOpen;
    private nint gameWindow;
    private Rect visibleBounds;
    private Geometry? visibleOutline;
    private double compactWidth = CompactMinWidth;
    private double musicSlotWidth = GameTrackWidth;
    private DisplayMode mode;
    private ForegroundWindow foreground;
    private ForegroundWindow pendingForeground;
    private ForegroundWindow lastApp;
    private ForegroundWindow ruleTarget;
    private long foregroundChangedAt;
    private HwndSource? source;
    private bool hotkeyRegistered;
    private bool animatingMorph;
    private TimeSpan? lastRenderTime;
    private double displayScale = 1;
    private double gameWidth = 760;
    private bool wrapTelemetry;
    private double GameHeight => (games.LargeText ? 32 : 26) * (wrapTelemetry ? 2 : 1);
    private bool focusPanelOnOpen;
    private bool live;
    private bool shuttingDown;
    private bool moveAfterDpiChange;
    private CancellationTokenSource? sampling;
    private Task? samplingCompletion;
    private string? presentMonPath;
    private PresentMonCapture? capture;
    private int capturePid;
    private string? captureStatus;
    private TemperatureReading temperatures;
    private string? sensorSetupPath;
    private bool islandHotkeyRegistered;

    public HudWindow(bool showPreview)
    {
        InitializeComponent();
        presentMonPath = FindPresentMon();
        sensorSetupPath = FindBundledFile("PawnIO_setup.exe");
        mode = showPreview ? DisplayMode.Preview : DisplayMode.Automatic;

        var menu = new Forms.ContextMenuStrip();
        automatic = AddMode(menu, "Automatic island", DisplayMode.Automatic);
        always = AddMode(menu, "Pin game HUD", DisplayMode.Always);
        hidden = AddMode(menu, "Hide island", DisplayMode.Hidden);
        preview = AddMode(menu, "Preview layout", DisplayMode.Preview);
        menu.Items.Add(new Forms.ToolStripSeparator());
        openIsland = new Forms.ToolStripMenuItem("Open island", null, (_, _) => TogglePanel());
        menu.Items.Add(openIsland);
        gameRule = new Forms.ToolStripMenuItem("Game detection");
        markGame = new Forms.ToolStripMenuItem("Treat this app as a game", null, (_, _) => SetGameOverride(true));
        ignoreApp = new Forms.ToolStripMenuItem("Never show the game HUD here", null, (_, _) => SetGameOverride(false));
        resetRule = new Forms.ToolStripMenuItem("Use automatic detection", null, (_, _) => SetGameOverride(null));
        gameRule.DropDownItems.AddRange([markGame, ignoreApp, resetRule]);
        menu.Items.Add(gameRule);

        var settings = new Forms.ToolStripMenuItem("Settings");
        graphOption = new Forms.ToolStripMenuItem("Frame time graph", null, (_, _) =>
        {
            games.ShowGraph = !games.ShowGraph;
            SaveSettings();
        });
        largeTextOption = new Forms.ToolStripMenuItem("Larger HUD text", null, (_, _) =>
        {
            games.LargeText = !games.LargeText;
            SaveSettings();
        });
        frameStatus = new Forms.ToolStripMenuItem { Enabled = false };
        temperatureStatus = new Forms.ToolStripMenuItem { Enabled = false };
        sensorSetup = new Forms.ToolStripMenuItem("Sensor setup…", null, (_, _) => OpenSensorInstaller());
        settings.DropDownItems.AddRange([graphOption, largeTextOption, new Forms.ToolStripSeparator(), frameStatus, temperatureStatus,
            new Forms.ToolStripMenuItem("Choose PresentMon…", null, (_, _) => ChoosePresentMon()), sensorSetup]);
        menu.Items.Add(settings);
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (Application.Current is App { CanUpdate: true } app)
        {
            menu.Items.Add("Check for updates", null, async (_, _) =>
            {
                var message = await app.CheckForUpdates();
                if (!shuttingDown) tray?.ShowBalloonTip(4000, Title, message, Forms.ToolTipIcon.Info);
            });
        }
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = Title,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ToggleExpanded();
        menu.Opening += (_, _) =>
        {
            ruleTarget = lastApp;
            RefreshMenu();
        };
        RefreshMenu();
        timer.Tick += (_, _) => Tick();
        hoverTimer.Tick += (_, _) => UpdatePanelHover(Environment.TickCount64);
        CompactContent.Click += (_, _) => TogglePanel();
        Deactivated += (_, _) => { if (panelPinned && panel?.HoldsOpen != true) DismissPanel(); };
        Island.AllowDrop = true;
        Island.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (e.Effects != DragDropEffects.None && shape != IslandShape.Game) PinPanel(activate: false);
        };
        Island.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && shape != IslandShape.Game)
            {
                PinPanel(activate: false);
                panel?.AddPaths(paths);
            }
            e.Handled = true;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        source = (HwndSource)PresentationSource.FromVisual(this);
        Desktop.ConfigureOverlay(source.Handle, true);
        source.AddHook(HandleWindowMessage);
        hotkeyRegistered = Desktop.RegisterToggle(source.Handle);
        islandHotkeyRegistered = Desktop.RegisterIslandToggle(source.Handle);
        panel = CreatePanel();
        pendingForeground = foreground = Desktop.ReadForeground();
        Tick();
        timer.Start();
        hoverTimer.Start();
    }

    private nint HandleWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Desktop.HotkeyMessage)
        {
            if (wParam == Desktop.HotkeyId) { handled = true; ToggleExpanded(); }
            else if (wParam == Desktop.IslandHotkeyId) { handled = true; TogglePanel(); }
        }
        if (message == 0x031D && clipboardListening) Dispatcher.BeginInvoke(() => panel?.RecordClipboard());
        if (message == 0x02E0 && !moveAfterDpiChange) // WM_DPICHANGED
        {
            moveAfterDpiChange = true;
            Dispatcher.BeginInvoke(() =>
            {
                moveAfterDpiChange = false;
                Position();
            }, DispatcherPriority.Loaded);
        }
        return 0;
    }

    private Forms.ToolStripMenuItem AddMode(Forms.ContextMenuStrip menu, string label, DisplayMode value)
    {
        var item = new Forms.ToolStripMenuItem(label);
        item.Click += (_, _) => SetMode(value);
        menu.Items.Add(item);
        return item;
    }

    private void ToggleExpanded() => SetMode(mode == DisplayMode.Always ? DisplayMode.Automatic : DisplayMode.Always);

    private void SetMode(DisplayMode value)
    {
        if (mode == value) return;
        if (gameMusicOpen) DismissPanel();
        mode = value;
        panelPinned = false;
        hoverStarted = leaveStarted = 0;
        if (value == DisplayMode.Hidden) mediaPeekUntil = timerPeekUntil = 0;
        Tick();
        RefreshMenu();
    }

    private void Tick()
    {
        panel?.Tick();
        var now = Environment.TickCount64;
        if (panel?.TimerCompleted == true && !timerCompleted && !live && mode == DisplayMode.Automatic)
            timerPeekUntil = now + 6_000;
        timerCompleted = panel?.TimerCompleted == true;

        var current = Desktop.ReadForeground(pendingForeground);
        if (current.ProcessId > 0) lastApp = current;
        if (gameMusicOpen && IsActive && current.ProcessId == 0) current = foreground;
        else if (panelPinned && IsActive && current.ProcessId == 0) current = foreground with { IsFullscreen = false };
        if (current != pendingForeground)
        {
            pendingForeground = current;
            foregroundChangedAt = now;
        }
        if (!current.IsFullscreen || now - foregroundChangedAt >= 350) foreground = pendingForeground;

        SetLive(mode == DisplayMode.Always || mode == DisplayMode.Automatic && games.IsFullscreenGame(foreground));
        if (gameMusicOpen && !live && mode != DisplayMode.Preview) DismissPanel();
        PreviewLabel.Visibility = mode == DisplayMode.Preview ? Visibility.Visible : Visibility.Collapsed;
        if (mode == DisplayMode.Preview)
        {
            ShowFrames(new FrameReading(144, 6.9, PreviewHistory));
            ShowUsage(new UsageReading(34, 86, 62));
            ShowTemperatures(new TemperatureReading(61, 72));
        }
        else if (live)
        {
            UpdateCapture(foreground.ProcessId);
            ShowFrames(capture?.Read());
        }

        Position();
        RefreshCompact(now);
        UpdatePanelHover(now);
    }

    private void Position()
    {
        if (source is null) return;
        ApplyDisplayWidth(Desktop.MonitorWidth(source.Handle, foreground.Monitor));
        Desktop.CenterAtTop(source.Handle, foreground.Monitor);
    }

    private void ApplyDisplayWidth(double available)
    {
        var previousWidth = gameWidth;
        var previousHeight = TelemetryContent.Height;
        var previousSlot = musicSlotWidth;
        if (available <= 0) available = WindowWidth;
        var usable = Math.Max(1, available - 16);
        var panelWidth = Math.Min(PanelWidth, Math.Max(328, usable - 16));
        panel?.ResizeTo(panelWidth);
        var minimumSingleRow = games.LargeText ? 650 : 560;
        if (mode == DisplayMode.Preview) minimumSingleRow += 40;
        gameWidth = Math.Min(Math.Max(400, usable), Math.Max(minimumSingleRow, GameWidthFor(available)));
        musicSlotWidth = Math.Min(GameTrackWidth, Math.Max(0, usable - gameWidth));
        if (musicSlotWidth < 48) musicSlotWidth = 0;
        wrapTelemetry = gameWidth < minimumSingleRow;
        SecondaryMetricsRow.Height = wrapTelemetry ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // On the two-row layout, CPU and GPU each use half the metrics area.
        FpsColumn.Width = new GridLength(wrapTelemetry ? .98 : .85, GridUnitType.Star);
        FrameColumn.Width = new GridLength(wrapTelemetry ? 1.02 : 1.15, GridUnitType.Star);
        CpuColumn.Width = new GridLength(wrapTelemetry ? .83 : 1.4, GridUnitType.Star);
        GpuColumn.Width = new GridLength(wrapTelemetry ? .82 : 1.4, GridUnitType.Star);
        RamColumn.Width = new GridLength(wrapTelemetry ? .35 : .95, GridUnitType.Star);
        Grid.SetRow(CpuGroup, wrapTelemetry ? 1 : 0);
        Grid.SetColumn(CpuGroup, wrapTelemetry ? 0 : 2);
        Grid.SetColumnSpan(CpuGroup, wrapTelemetry ? 2 : 1);
        Grid.SetRow(GpuGroup, wrapTelemetry ? 1 : 0);
        Grid.SetColumn(GpuGroup, wrapTelemetry ? 2 : 3);
        Grid.SetColumnSpan(GpuGroup, wrapTelemetry ? 3 : 1);
        Grid.SetColumnSpan(FrameGroup, wrapTelemetry ? 2 : 1);
        Grid.SetColumn(RamGroup, wrapTelemetry ? 3 : 4);
        Grid.SetColumnSpan(RamGroup, wrapTelemetry ? 2 : 1);
        TelemetryContent.Height = GameHeight;
        TextElement.SetFontSize(TelemetryContent, games.LargeText ? 16 : 13);
        Graph.Visibility = games.ShowGraph && gameWidth >= minimumSingleRow + 96 ? Visibility.Visible : Visibility.Collapsed;
        var stageWidth = Math.Max(panelWidth, gameWidth + musicSlotWidth) + 16;
        var scale = Math.Min(1, available / stageWidth);
        if (Math.Abs(Scene.Width - stageWidth) > 0.001 || Math.Abs(scale - displayScale) > 0.001)
        {
            displayScale = scale;
            Scene.Width = stageWidth;
            Width = stageWidth * scale;
            Height = WindowHeight * scale;
            Scene.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
            DrawMorph(width.Value, height.Value, emergence.Value);
        }
        if (IsGameShape && (previousWidth != gameWidth || previousHeight != GameHeight || previousSlot != musicSlotWidth)) StartMorph();
    }

    internal static double GameWidthFor(double monitorWidth) => Math.Clamp(Math.Round(monitorWidth * .4), 560, MaxGameWidth);

    private void RefreshCompact(long now)
    {
        var timerText = now < timerPeekUntil ? "Time’s up"
            : now < mediaPeekUntil ? null : panel?.CompactTimerText;
        var notice = now < mediaPeekUntil ? playbackNotice : null;
        var title = timerText ?? notice ?? (currentMedia.HasTrack ? currentMedia.Title : "Next Island");
        if (CompactTitle.Text != title)
        {
            CompactTitle.Text = title;
            var sample = title[..Math.Min(title.Length, 96)];
            var measure = new FormattedText(sample, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(CompactTitle.FontFamily, CompactTitle.FontStyle, CompactTitle.FontWeight, CompactTitle.FontStretch),
                CompactTitle.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var target = Math.Clamp(Math.Ceiling(measure.Width + 83), CompactMinWidth, CompactMaxWidth);
            if (target != compactWidth)
            {
                compactWidth = target;
                if (shape == IslandShape.Pill) StartMorph();
            }
        }
        var playing = timerText is null && currentMedia.Playing;
        CompactArt.Visibility = timerText is null && (currentMedia.Artwork is not null || notice == "Playback ended" && CompactArt.Fill is not null)
            ? Visibility.Visible : Visibility.Collapsed;
        CompactPlayback.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        if (timerText is null && (currentMedia.HasTrack || notice == "Paused")) CompactPlayback.Visibility = Visibility.Visible;
        CompactPlayback.SetPlaying(playing);
        CompactClock.Visibility = CompactPlayback.Visibility == Visibility.Visible || notice is not null || now < timerPeekUntil
            ? Visibility.Collapsed : Visibility.Visible;
        CompactClock.Text = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }

    private void ReceiveMedia(MediaReading reading, PlaybackChange change)
    {
        if (shuttingDown) return;
        var now = Environment.TickCount64;
        if (!live && mode == DisplayMode.Automatic)
        {
            switch (change)
            {
                case PlaybackChange.Paused:
                case PlaybackChange.Ended:
                    playbackNotice = change == PlaybackChange.Paused ? "Paused" : "Playback ended";
                    mediaPeekUntil = now + 2_000;
                    break;
                case PlaybackChange.Started:
                case PlaybackChange.Resumed:
                case PlaybackChange.Next:
                case PlaybackChange.Previous:
                case PlaybackChange.Advanced:
                case PlaybackChange.Changed when reading.Playing:
                case PlaybackChange.Restarted:
                    playbackNotice = null;
                    mediaPeekUntil = now + (change is PlaybackChange.Resumed or PlaybackChange.Restarted ? 2_500 : 4_500);
                    break;
                case PlaybackChange.Stopped:
                    playbackNotice = null;
                    mediaPeekUntil = 0;
                    break;
            }
        }
        else
        {
            playbackNotice = null;
            mediaPeekUntil = 0;
        }

        if (change == PlaybackChange.Ended && !reading.HasTrack && currentMedia.Artwork is { } endingArt)
            CompactArt.Fill = new ImageBrush(endingArt) { Stretch = Stretch.UniformToFill };
        else if ((CompactArt.Fill as ImageBrush)?.ImageSource != reading.Artwork && (reading.HasTrack || now >= mediaPeekUntil))
            CompactArt.Fill = reading.Artwork is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : null;
        currentMedia = reading;
        if (reading.HasTrack)
        {
            GameTrackTitle.Text = reading.Title;
            GameTrackTitle.ToolTip = reading.Title;
            if ((GameArt.Fill as ImageBrush)?.ImageSource != reading.Artwork)
                GameArt.Fill = reading.Artwork is { } gameArt ? new ImageBrush(gameArt) { Stretch = Stretch.UniformToFill } : null;
            GameArt.Visibility = reading.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
        }
        GamePlayback.SetPlaying(reading.Playing);
        if (IsGameShape && PlaybackMotion.ReplacesTrack(change) && reading.HasTrack)
            PlaybackMotion.Arrive(GameTrackTitle, PlaybackMotion.Offset(change, 6), 170);
        if (IsGameShape) UpdateGameTrack();
        RefreshCompact(now);
        UpdatePanelHover(now);
        if (shape == IslandShape.Pill && change != PlaybackChange.None)
        {
            var offset = PlaybackMotion.Offset(change, 6);
            var duration = change == PlaybackChange.Advanced ? 225 : 160;
            PlaybackMotion.Arrive(CompactTile, offset, duration);
            PlaybackMotion.Arrive(CompactTitle, offset, duration);
        }
    }

    private IslandPanel CreatePanel()
    {
        var content = new IslandPanel(SetClipboardListening, ReceiveMedia);
        content.ViewChanged += () => { if (shape is IslandShape.Panel or IslandShape.GameMusic) StartMorph(); };
        PanelViewport.Children.Add(content);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && shape is IslandShape.Panel or IslandShape.GameMusic)
            {
                e.Handled = true;
                DismissPanel();
            }
        };
        return content;
    }

    private void TogglePanel()
    {
        if (gameMusicOpen)
        {
            DismissPanel();
            return;
        }
        if (live || mode == DisplayMode.Preview)
        {
            gameMusicOpen = true;
            gameWindow = Desktop.ForegroundHandle;
            panel?.SetGameMusicMode(true);
            PinPanel(activate: true);
            return;
        }
        if (mode != DisplayMode.Automatic) SetMode(DisplayMode.Automatic);
        if (panelPinned) DismissPanel();
        else
        {
            var now = Environment.TickCount64;
            if (now < timerPeekUntil) panel?.SelectTimer();
            else if (now < mediaPeekUntil) panel?.SelectMusic();
            PinPanel(activate: true);
        }
    }

    private void PinPanel(bool activate)
    {
        panelPinned = true;
        focusPanelOnOpen = activate;
        dismissedUntilLeave = false;
        SetShape(gameMusicOpen ? IslandShape.GameMusic : IslandShape.Panel);
        if (activate && source is not null)
        {
            Desktop.AllowActivation(source.Handle);
            Activate();
            if (!animatingMorph)
            {
                panel?.FocusControls();
                focusPanelOnOpen = false;
            }
        }
    }

    private void DismissPanel()
    {
        var returnFocus = gameMusicOpen && IsActive ? gameWindow : 0;
        gameMusicOpen = false;
        gameWindow = 0;
        panel?.SetGameMusicMode(false);
        panelPinned = false;
        focusPanelOnOpen = false;
        dismissedUntilLeave = true;
        hoverStarted = leaveStarted = 0;
        mediaPeekUntil = timerPeekUntil = 0;
        SetShape(live || mode == DisplayMode.Preview ? IslandShape.Game : IslandShape.Rest);
        Desktop.RestoreForeground(returnFocus);
    }

    private void UpdatePanelHover(long now)
    {
        if (source is null || shuttingDown) return;
        if (gameMusicOpen && (live || mode == DisplayMode.Preview))
        {
            if (panel?.HoldsOpen != true && visibleBounds.Height > GameHeight + 10 &&
                Desktop.ClickedOutside(source.Handle, visibleBounds, displayScale, visibleOutline)) DismissPanel();
            return;
        }
        if (mode == DisplayMode.Hidden || live || mode == DisplayMode.Preview)
        {
            panelPinned = false;
            focusPanelOnOpen = false;
            hoverStarted = leaveStarted = 0;
            SetShape(mode == DisplayMode.Hidden ? IslandShape.Rest : IslandShape.Game);
            return;
        }

        if (choosingFile) return;
        if (panelPinned)
        {
            if (panel?.HoldsOpen != true && visibleBounds.Height > 32 &&
                Desktop.ClickedOutside(source.Handle, visibleBounds, displayScale, visibleOutline)) DismissPanel();
            return;
        }
        var overEdge = Desktop.CursorOver(source.Handle, CompactMinWidth * displayScale, 0,
            (shape == IslandShape.Pill ? 23 : 10) * displayScale);
        // The pointer can catch the surface during retraction and reverse the spring.
        var overIsland = (animatingMorph || shape is IslandShape.Pill or IslandShape.Panel) && visibleBounds.Height > 1
            && Desktop.CursorOver(source.Handle, visibleBounds.Width * displayScale,
                visibleBounds.Top * displayScale, visibleBounds.Height * displayScale);
        UpdateHoverShape(now, overEdge || overIsland);
    }

    private void UpdateHoverShape(long now, bool hovering)
    {
        if (dismissedUntilLeave)
        {
            if (hovering) return;
            dismissedUntilLeave = false;
        }

        if (hovering)
        {
            leaveStarted = 0;
            if (hoverStarted == 0) hoverStarted = now;
            if (shape == IslandShape.Pill || now - hoverStarted >= HoverIntentMs) SetShape(IslandShape.Pill);
            return;
        }

        hoverStarted = 0;
        if (leaveStarted == 0) leaveStarted = now;
        if (shape == IslandShape.Pill && now - leaveStarted < HoverExitMs) return;
        SetShape(now < mediaPeekUntil || now < timerPeekUntil ? IslandShape.Pill : IslandShape.Rest);
    }

    private void SetClipboardListening(bool enabled)
    {
        if (source is null || enabled == clipboardListening) return;
        if (enabled) clipboardListening = Desktop.ObserveClipboard(source.Handle);
        else
        {
            Desktop.StopObservingClipboard(source.Handle);
            clipboardListening = false;
        }
    }

    private void SetShape(IslandShape target)
    {
        if (shape == target) return;
        shape = target;
        showGameTrack = IsGameShape && currentMedia.HasTrack && !currentMedia.Stopped;
        if (source is not null) Desktop.ConfigureOverlay(source.Handle, target is IslandShape.Game or IslandShape.Rest);
        StartMorph();
    }

    private bool IsGameShape => shape is IslandShape.Game or IslandShape.GameMusic;

    private void UpdateGameTrack()
    {
        var show = currentMedia.HasTrack && !currentMedia.Stopped;
        if (show == showGameTrack) return;
        showGameTrack = show;
        StartMorph();
    }

    private void StartMorph()
    {
        var (targetWidth, targetHeight) = ShapeSize(shape);
        if (!SystemParameters.ClientAreaAnimation || source is null)
        {
            StopMorph();
            width = new(targetWidth);
            height = new(targetHeight);
            emergence = new(shape == IslandShape.Rest ? 0 : 1);
            gameProgress = new(IsGameShape ? 1 : 0);
            musicProgress = new(showGameTrack ? 1 : 0);
            DrawMorph(width.Value, height.Value, emergence.Value);
        }
        else if (!animatingMorph)
        {
            animatingMorph = true;
            CompositionTarget.Rendering += AnimateMorph;
        }
    }

    private (double Width, double Height) ShapeSize(IslandShape value) => value switch
    {
        IslandShape.Panel => (panel?.Width ?? PanelWidth, panel?.DesiredPanelHeight ?? 168),
        IslandShape.Game => (gameWidth + (showGameTrack ? musicSlotWidth : 0), GameHeight),
        IslandShape.GameMusic => (gameWidth + (showGameTrack ? musicSlotWidth : 0), GameHeight + (panel?.DesiredPanelHeight ?? 168)),
        IslandShape.Pill => (compactWidth, PillHeight),
        _ => (100, 0)
    };

    private void AnimateMorph(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        var dt = lastRenderTime is { } last ? (time - last).TotalSeconds : 1d / 60;
        lastRenderTime = time;
        if (dt <= 0) return;

        var (targetWidth, targetHeight) = ShapeSize(shape);
        var reducedMotion = !SystemParameters.ClientAreaAnimation;
        var frequency = shape switch
        {
            IslandShape.Rest => 32d,
            IslandShape.Panel or IslandShape.GameMusic => 22d,
            _ => 26d
        };
        var widthDone = width.Advance(targetWidth, dt, frequency, 0.35, reducedMotion);
        var heightDone = height.Advance(targetHeight, dt, frequency, 0.35, reducedMotion);
        var emergenceDone = emergence.Advance(shape == IslandShape.Rest ? 0 : 1, dt, frequency, 0.002, reducedMotion);
        var gameDone = gameProgress.Advance(IsGameShape ? 1 : 0, dt, frequency, 0.002, reducedMotion);
        var musicDone = musicProgress.Advance(showGameTrack ? 1 : 0, dt, frequency, 0.002, reducedMotion);
        DrawMorph(width.Value, height.Value, emergence.Value);
        if (widthDone && heightDone && emergenceDone && gameDone && musicDone) StopMorph();
    }

    private void StopMorph()
    {
        if (animatingMorph) CompositionTarget.Rendering -= AnimateMorph;
        animatingMorph = false;
        lastRenderTime = null;
    }

    private void DrawMorph(double bodyWidth, double bodyHeight, double reveal)
    {
        bodyWidth = Math.Clamp(bodyWidth, 100, Math.Max(100, Scene.Width - 4));
        bodyHeight = Math.Clamp(bodyHeight, 0, WindowHeight - 12);
        var game = Math.Clamp(gameProgress.Value, 0, 1);
        var music = Math.Clamp(musicProgress.Value, 0, 1);
        var top = (9 + (GameGap - 9) * game) * Ease(0.04, 0.90, reveal);
        var neck = Ease(0.06, 0.30, reveal) * (1 - Ease(0.55, 0.88, reveal));
        var radius = Math.Min(bodyHeight / 2, 19 + 7 * Ease(50, 150, bodyHeight));
        visibleBounds = new Rect((Scene.Width - bodyWidth) / 2, top, bodyWidth, bodyHeight);
        var musicWidth = musicSlotWidth * music;
        var outline = IslandSurface.CreateBody(bodyWidth, bodyHeight, radius, game, GameHeight, panel?.Width ?? PanelWidth);
        visibleOutline = outline;
        Membrane.SetShape(visibleBounds, outline, neck, music * game, GameHeight, musicWidth);

        Island.Width = bodyWidth;
        Island.Height = bodyHeight;
        Canvas.SetLeft(Island, visibleBounds.X);
        Canvas.SetTop(Island, top);
        Island.Clip = outline;
        var compact = Ease(0.25, 0.79, reveal) * Ease(24, 36, bodyHeight)
            * (1 - Ease(40, 83, bodyHeight)) * (1 - Ease(.05, .55, game));
        var telemetry = Ease(gameWidth * .74, gameWidth * .97, bodyWidth) * Ease(0.42, 0.92, game);
        var panelAlpha = Ease(32, 108, bodyHeight - GameHeight * game) * Ease(216, 326, bodyWidth);

        CompactContent.Opacity = compact;
        CompactContent.Width = Math.Min(compactWidth, bodyWidth);
        CompactContent.Visibility = compact > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactContent.IsHitTestVisible = shape == IslandShape.Pill && compact > .45;
        TelemetryContent.Opacity = telemetry;
        TelemetryContent.Visibility = telemetry > 0 ? Visibility.Visible : Visibility.Collapsed;
        TelemetryContent.Width = gameWidth + musicWidth;
        MusicColumn.Width = new GridLength(musicWidth);
        GameTrack.Width = Math.Max(0, musicSlotWidth);
        GameTrack.Opacity = Ease(.20, .88, music);
        GameTrack.Visibility = music > .02 ? Visibility.Visible : Visibility.Collapsed;
        PanelViewport.Margin = new Thickness(0, GameHeight * game, 0, 0);
        PanelViewport.Opacity = panelAlpha;
        PanelViewport.Visibility = panelAlpha > 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelViewport.IsHitTestVisible = panelAlpha > 0.40 && shape is IslandShape.Panel or IslandShape.GameMusic;
        if (focusPanelOnOpen && IsActive && panelAlpha > 0.65)
        {
            focusPanelOnOpen = false;
            panel?.FocusControls();
        }
    }

    private static double Ease(double start, double end, double value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private struct Spring(double initial)
    {
        internal double Value = initial;
        private double velocity;

        internal bool Advance(double goal, double dt, double frequency, double threshold, bool settle)
        {
            var distance = Value - goal;
            var momentum = velocity + frequency * distance;
            var decay = Math.Exp(-frequency * dt);
            Value = goal + (distance + momentum * dt) * decay;
            velocity = (velocity - frequency * momentum * dt) * decay;
            if (!settle && (Math.Abs(Value - goal) > threshold || Math.Abs(velocity) > threshold * frequency))
                return false;
            Value = goal;
            velocity = 0;
            return true;
        }
    }

    private void SetLive(bool value)
    {
        if (live == value) return;
        live = value;
        if (!value)
        {
            if (sampling is { } stopped && samplingCompletion is { } completion)
            {
                stopped.Cancel();
                _ = completion.ContinueWith(_ => stopped.Dispose(), TaskScheduler.Default);
            }
            sampling = null;
            samplingCompletion = null;
            StopCapture();
            capturePid = 0;
            return;
        }

        ShowFrames(null);
        ShowUsage(default);
        ShowTemperatures(default);
        var session = new CancellationTokenSource();
        sampling = session;
        var usage = Task.Run(() => SampleUsage(session.Token));
        var temps = Task.Run(() => SampleTemperatures(session.Token));
        samplingCompletion = Task.WhenAll(usage, temps);
    }

    private async Task SampleUsage(CancellationToken cancellation)
    {
        using var hardware = new HardwareTelemetry();
        try
        {
            using var interval = new PeriodicTimer(TimeSpan.FromMilliseconds(800));
            do
            {
                var reading = hardware.Read();
                await Dispatcher.InvokeAsync(() => ShowUsage(reading), DispatcherPriority.Background, cancellation);
            }
            while (await interval.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException) { }
    }

    private async Task SampleTemperatures(CancellationToken cancellation)
    {
        using var sensors = new TemperatureSensors();
        try
        {
            do
            {
                var reading = sensors.Read();
                await Dispatcher.InvokeAsync(() => ShowTemperatures(reading), DispatcherPriority.Background, cancellation);
                var delay = reading.Cpu is null && reading.Gpu is null ? 8 : 3;
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellation);
            }
            while (!cancellation.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateCapture(int processId)
    {
        if (processId != capturePid)
        {
            StopCapture();
            capturePid = processId;
            captureStatus = null;
            if (processId > 0 && presentMonPath is { } path)
                capture = new PresentMonCapture(path, processId);
        }
        if (capture?.Exited == true)
        {
            var permissionDenied = capture.PermissionDenied;
            StopCapture();
            captureStatus = permissionDenied ? "FPS: Windows denied capture access"
                : "FPS: capture unavailable for this app";
        }
        closingCaptures.RemoveAll(task => task.IsCompleted);
    }

    private void StopCapture()
    {
        if (capture is null) return;
        capture.Dispose();
        closingCaptures.Add(capture.Completion);
        capture = null;
    }

    private void ShowFrames(FrameReading? reading)
    {
        Fps.Text = Format(reading?.FramesPerSecond, "0", "");
        SetMetric(FrameTimeNumber, FrameTimeUnit, reading?.Milliseconds, "0.0", " ms");
        if (games.ShowGraph) Graph.SetSamples(reading?.History ?? Array.Empty<double>());
    }

    private void ShowUsage(UsageReading reading)
    {
        SetMetric(CpuNumber, CpuUnit, reading.Cpu, "0", "%");
        SetMetric(GpuNumber, GpuUnit, reading.Gpu, "0", "%");
        SetMetric(RamNumber, RamUnit, reading.Ram, "0", "%");
    }

    private void ShowTemperatures(TemperatureReading reading)
    {
        temperatures = reading;
        SetMetric(CpuTempNumber, CpuTempUnit, reading.Cpu, "0", "°C");
        SetMetric(GpuTempNumber, GpuTempUnit, reading.Gpu, "0", "°C");
    }

    private static void SetMetric(Run number, Run unit, double? value, string precision, string suffix)
    {
        number.Text = Format(value, precision, "");
        unit.Text = value.HasValue ? suffix : "";
    }

    private static string Format(double? number, string precision, string unit) =>
        number is { } value ? value.ToString(precision, System.Globalization.CultureInfo.CurrentCulture) + unit : "—";

    private void RefreshMenu()
    {
        automatic.Checked = mode == DisplayMode.Automatic;
        always.Checked = mode == DisplayMode.Always;
        hidden.Checked = mode == DisplayMode.Hidden;
        preview.Checked = mode == DisplayMode.Preview;
        always.ShortcutKeyDisplayString = hotkeyRegistered ? "Ctrl+Alt+F10" : "";
        openIsland.ShortcutKeyDisplayString = islandHotkeyRegistered ? "Ctrl+Alt+F9" : "";
        var identity = GameDetection.IdentityFor(ruleTarget);
        gameRule.Enabled = identity is not null;
        gameRule.Text = gameRule.Enabled ? $"Game detection for {ruleTarget.Name}" : "Game detection";
        var appRule = games.OverrideFor(identity);
        markGame.Checked = appRule == true;
        ignoreApp.Checked = appRule == false;
        resetRule.Checked = appRule is null;
        graphOption.Checked = games.ShowGraph;
        largeTextOption.Checked = games.LargeText;
        sensorSetup.Enabled = sensorSetupPath is not null;
        UpdateProviderLabels();
    }

    private void SetGameOverride(bool? isGame)
    {
        if (GameDetection.IdentityFor(ruleTarget) is not { } identity) return;
        if (!games.SetOverride(identity, isGame)) ShowSettingsError();
        RefreshMenu();
    }

    private void SaveSettings()
    {
        if (!games.Save()) ShowSettingsError();
        Tick();
        RefreshMenu();
    }

    private void ShowSettingsError() =>
        tray.ShowBalloonTip(3500, Title, "Settings could not be saved. Changes apply until you quit.", Forms.ToolTipIcon.Warning);

    private void UpdateProviderLabels()
    {
        var frames = presentMonPath is null ? "FPS: PresentMon required"
            : captureStatus ?? (live && capture?.Read() is not null ? "FPS: capturing frame times"
                : live ? "FPS: waiting for frames" : "FPS: PresentMon ready");
        var sensors = live
            ? temperatures is { Cpu: not null, Gpu: not null } ? "Temperatures: CPU and GPU detected"
                : temperatures.Cpu is not null || temperatures.Gpu is not null ? "Temperatures: one sensor available"
                : "Temperatures: no readings from this PC"
            : "Temperatures: sampled in game HUD";
        frameStatus.Text = frames;
        temperatureStatus.Text = sensors;
    }

    private void ChoosePresentMon()
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose the PresentMon console executable",
            Filter = "PresentMon executable (PresentMon*.exe)|PresentMon*.exe",
            CheckFileExists = true
        };
        choosingFile = true;
        bool? accepted;
        try { accepted = picker.ShowDialog(); }
        finally { choosingFile = false; }
        if (accepted != true) return;
        presentMonPath = picker.FileName;
        StopCapture();
        capturePid = 0;
        captureStatus = null;
        Tick();
        RefreshMenu();
    }

    private static string? FindPresentMon()
    {
        var configured = Environment.GetEnvironmentVariable("SAPPHIRE_PRESENTMON");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
            return configured;
        return FindBundledFile("PresentMon-2.5.1-x64.exe");
    }

    private static string? FindBundledFile(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Providers", name);
        return File.Exists(path) ? path : null;
    }

    private void OpenSensorInstaller()
    {
        if (sensorSetupPath is not { } path) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            tray.ShowBalloonTip(3500, Title, "Sensor setup could not open.", Forms.ToolTipIcon.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!shuttingDown)
        {
            shuttingDown = true;
            timer.Stop();
            hoverTimer.Stop();
            StopMorph();
            SetLive(false);
            panel?.Dispose();
            if (source is not null)
            {
                if (hotkeyRegistered) Desktop.UnregisterToggle(source.Handle);
                if (islandHotkeyRegistered) Desktop.UnregisterIslandToggle(source.Handle);
                SetClipboardListening(false);
                source.RemoveHook(HandleWindowMessage);
            }
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
            if (closingCaptures.Count != 0)
                Task.WhenAll(closingCaptures).Wait(TimeSpan.FromSeconds(2));
        }
        base.OnClosed(e);
    }
}
