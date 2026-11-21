// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the Windows island.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SapphireGameHud;

public partial class IslandPanel : UserControl, IDisposable
{
    private enum View { Music, Shelf, Clipboard, Timer, Settings }

    private sealed record ShelfItem(string Path, bool IsDirectory, string SizeLabel)
    {
        public string Label => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));

        internal static ShelfItem FromPath(string path)
        {
            if (Directory.Exists(path)) return new(path, true, "Folder");
            try
            {
                var length = new FileInfo(path).Length;
                return new(path, false, FormatSize(length));
            }
            catch (IOException)
            {
                return new(path, false, "");
            }
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
            _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB"
        };
    }

    private sealed record ClipboardEntry(string Text, DateTimeOffset CapturedAt, bool Pinned = false)
    {
        public string Label => Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;
        public string AgeLabel
        {
            get
            {
                var elapsed = DateTimeOffset.UtcNow - CapturedAt;
                if (elapsed.TotalSeconds < 60) return "now";
                if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m";
                if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h";
                return $"{(int)elapsed.TotalDays}d";
            }
        }
    }

    private readonly ObservableCollection<ShelfItem> shelf = [];
    private readonly List<ClipboardEntry> clipboard = [];
    private readonly ObservableCollection<ClipboardEntry> visibleClipboard = [];
    private readonly DispatcherTimer seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Action<bool> listenToClipboard;
    private readonly WindowsMedia media;
    private readonly SystemAudio audio = new();
    private View view;
    private View previousView;
    private bool loadingSettings;
    private MediaReading reading = MediaReading.Empty;
    private long receivedAt;
    private (TrackDirection Direction, long Expires)? trackIntent;
    private long seekIntentUntil;
    private readonly ScaleTransform artworkScale = new(1, 1);
    private bool updatingSeek;
    private bool updatingVolume;
    private bool seeking;
    private bool showRemainingTime;
    private Point? fileDragOrigin;
    private bool fileDragActive;
    private bool pickerOpen;
    private double remaining = 25 * 60;
    private double selectedDuration = 25 * 60;
    private long timerEndsAt;
    private bool timerCompleted;
    private long copyFeedbackUntil;
    private int mediaCommandVersion;
    private bool audioAvailable;
    private bool audioMuted;

    internal IslandPanel(Action<bool> listenToClipboard, Action<MediaReading, PlaybackChange> compactMedia)
    {
        this.listenToClipboard = listenToClipboard;
        InitializeComponent();
        AlbumArt.RenderTransformOrigin = new Point(.5, .5);
        AlbumArt.RenderTransform = artworkScale;
        Files.ItemsSource = shelf;
        Clips.ItemsSource = visibleClipboard;
        Files.SelectionChanged += (_, _) => UpdateShelfButtons();
        Clips.SelectionChanged += (_, _) => UpdateClipboardButtons();
        Loaded += (_, _) =>
        {
            UpdateSelection(false);
            SyncVolume();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ViewTransitionLayer.Children.Clear();
            else SyncVolume();
        };
        PanelBody.SizeChanged += (_, _) => ViewChanged?.Invoke();
        seekDebounce.Tick += async (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed && Seek.IsMouseCaptureWithin) return;
            await CommitSeek();
        };
        SyncVolume();
        media = new WindowsMedia(value => _ = Dispatcher.InvokeAsync(() =>
        {
            compactMedia(value, UpdateMedia(value));
        }));
    }

    internal Color CurrentAccent { get; private set; } = ArtworkPalette.DefaultAccent;

    internal bool HoldsOpen => fileDragActive || pickerOpen || Seek.IsMouseCaptureWithin || VolumeSlider.IsMouseCaptureWithin
        || DisplayChoice.IsDropDownOpen || GameRuleChoice.IsDropDownOpen;

    internal event Action? ViewChanged;

    internal double DesiredPanelHeight
    {
        get
        {
            // Measure the control as a whole. Measuring only its child leaves the
            // parent's desired size stale after navigation and can clip actions.
            Measure(new Size(Width, double.PositiveInfinity));
            // WPF already caches valid measurements. A separate cached height can
            // outlive deferred template realization, font changes, or list updates.
            return Math.Ceiling(DesiredSize.Height);
        }
    }

    internal void ResizeTo(double width)
    {
        if (Width == width) return;
        Width = width;
        ClipboardTabIcon.Visibility = width < 360 ? Visibility.Collapsed : Visibility.Visible;
        OnViewChanged();
    }

    internal void FocusControls()
    {
        var firstControl = view switch
        {
            View.Shelf => AddFile,
            View.Clipboard => (Control)ClipboardEnabled,
            View.Timer => TimerButton,
            View.Settings => SettingsButton,
            _ when Toggle.IsEnabled => Toggle,
            _ when Navigation.Visibility == Visibility.Visible => MusicTab,
            _ => this
        };
        firstControl.Focus();
    }

    internal void SelectTimer() => ShowView(View.Timer);
    internal void SelectMusic() => ShowView(View.Music);

    internal void SetGameMusicMode(bool enabled)
    {
        if (enabled) SelectMusic();
        Navigation.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        NavigationHeader.Margin = new Thickness(0, 0, 0, enabled ? 2 : 16);
        SettingsOptions.MaxHeight = enabled ? 72 : 127;
        ViewHost.MinHeight = enabled ? 0 : 164;
        OnViewChanged();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            View? destination = e.Key switch
            {
                Key.D1 when Navigation.Visibility == Visibility.Visible => View.Music,
                Key.D2 when Navigation.Visibility == Visibility.Visible => View.Shelf,
                Key.D3 when Navigation.Visibility == Visibility.Visible => View.Clipboard,
                Key.D4 when Navigation.Visibility == Visibility.Visible => View.Timer,
                _ => null
            };
            if (destination is { } target)
            {
                ShowView(target);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.M && view == View.Music)
            {
                ToggleMute(this, e);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None
            && Keyboard.FocusedElement is not TextBox and not ButtonBase and not Slider)
        {
            if (view == View.Music && Toggle.IsEnabled)
            {
                ToggleTrack(this, e);
                e.Handled = true;
            }
            else if (view == View.Timer)
            {
                ToggleTimer(this, e);
                e.Handled = true;
            }
        }
    }

    private void NavigationSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSelection(false);

    private void UpdateSelection(bool animate)
    {
        if (SelectionIndicator is null || Navigation.ActualWidth <= 0) return;
        SelectionIndicator.Visibility = view == View.Settings ? Visibility.Collapsed : Visibility.Visible;
        var slot = Navigation.ActualWidth / 4;
        SelectionIndicator.Width = Math.Max(0, slot - 6);
        PlaybackMotion.EaseTo(SelectionOffset, TranslateTransform.XProperty, (int)view * slot, 220,
            animate && IsVisible && SystemParameters.ClientAreaAnimation);
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && sender is RadioButton { Tag: string name } && Enum.TryParse<View>(name, out var destination))
            ShowView(destination);
    }

    private void ShowView(View destination, bool moveFocus = false)
    {
        if (view == destination) return;
        var direction = Math.Sign(destination - view);
        CrossfadeView(direction);
        view = destination;
        MusicView.Visibility = ViewVisibility(View.Music);
        ShelfView.Visibility = ViewVisibility(View.Shelf);
        ClipboardView.Visibility = ViewVisibility(View.Clipboard);
        TimerView.Visibility = ViewVisibility(View.Timer);
        SettingsView.Visibility = ViewVisibility(View.Settings);
        MusicTab.IsChecked = view == View.Music;
        ShelfTab.IsChecked = view == View.Shelf;
        ClipboardTab.IsChecked = view == View.Clipboard;
        TimerTab.IsChecked = view == View.Timer;
        SettingsButton.Background = view == View.Settings ? (Brush)FindResource("ControlFill") : Brushes.Transparent;
        UpdateSelection(true);
        var incoming = view switch
        {
            View.Shelf => ShelfView, View.Clipboard => ClipboardView, View.Timer => TimerView,
            View.Settings => SettingsView, _ => MusicView
        };
        PlaybackMotion.Arrive(incoming, direction * 4, 180, 0);
        OnViewChanged();
        if (moveFocus) FocusControls();
    }

    private void CrossfadeView(int direction)
    {
        if (!IsVisible || !SystemParameters.ClientAreaAnimation || ViewHost.ActualWidth < 1 || ViewHost.ActualHeight < 1)
        {
            ViewTransitionLayer.Children.Clear();
            return;
        }

        // Capture the current composition, including an interrupted transition.
        // A noninteractive Canvas keeps outgoing content out of measurement and focus.
        var dpi = VisualTreeHelper.GetDpi(ViewHost);
        var snapshot = new RenderTargetBitmap((int)Math.Ceiling(ViewHost.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(ViewHost.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        snapshot.Render(ViewHost);
        snapshot.Freeze();
        var ghost = new Image
        {
            Source = snapshot, Width = ViewHost.ActualWidth, Height = ViewHost.ActualHeight,
            RenderTransform = new TranslateTransform(), IsHitTestVisible = false
        };
        ViewTransitionLayer.Children.Clear();
        ViewTransitionLayer.Children.Add(ghost);
        var fade = PlaybackMotion.To(1, 0, 140);
        fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd;
        fade.Completed += (_, _) => ViewTransitionLayer.Children.Remove(ghost);
        ghost.BeginAnimation(OpacityProperty, fade);
        PlaybackMotion.EaseTo((TranslateTransform)ghost.RenderTransform, TranslateTransform.XProperty, -direction * 4, 140, true);
    }

    private HudWindow? Hud => Window.GetWindow(this) as HudWindow;

    private void ToggleSettings(object sender, RoutedEventArgs e)
    {
        if (view == View.Settings)
        {
            ShowView(previousView);
            return;
        }
        previousView = view;
        Hud?.ReadIslandSettings(this);
        ShowView(View.Settings);
    }

    internal void LoadSettings(HudWindow.DisplayMode mode, bool graph, bool largeText, string? appName,
        bool? rule, bool sensorSetup, string providerStatus)
    {
        loadingSettings = true;
        try
        {
            GraphSwitch.IsChecked = graph;
            LargeTextSwitch.IsChecked = largeText;
            DisplayChoice.SelectedIndex = (int)mode;
            RuleAppName.Text = appName is null ? "No app selected for game detection" : $"Game detection for {appName}";
            GameRuleChoice.IsEnabled = appName is not null;
            GameRuleChoice.SelectedIndex = rule is null ? 0 : rule.Value ? 1 : 2;
            SensorSetupButton.IsEnabled = sensorSetup;
            ProviderStatus.Text = providerStatus;
        }
        finally { loadingSettings = false; }
    }

    private void ChangeGraphSetting(object sender, RoutedEventArgs e) => Hud?.ChangeHudOption(true, GraphSwitch.IsChecked == true);
    private void ChangeTextSetting(object sender, RoutedEventArgs e) => Hud?.ChangeHudOption(false, LargeTextSwitch.IsChecked == true);

    private void ChangeDisplayMode(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingSettings && DisplayChoice.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<HudWindow.DisplayMode>(tag, out var mode))
            Hud?.ChangeIslandMode(mode);
    }

    private void ChangeGameRule(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingSettings && GameRuleChoice.SelectedIndex >= 0)
            Hud?.ChangeGameRule(GameRuleChoice.SelectedIndex switch { 1 => true, 2 => false, _ => null });
    }

    private void ChooseFrameProvider(object sender, RoutedEventArgs e)
    {
        Hud?.ChoosePresentMon();
        Hud?.ReadIslandSettings(this);
    }

    private void OpenSensors(object sender, RoutedEventArgs e) => Hud?.OpenSensorInstaller();

    private async void CheckForUpdates(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        SetStatus(UpdateStatus, "Checking for updates…");
        try
        {
            var message = Application.Current is App app
                ? await app.CheckForUpdates()
                : "Install Next Island to enable updates.";
            SetStatus(UpdateStatus, message);
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    private void QuitApp(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void OnViewChanged()
    {
        InvalidateMeasure();
        ViewChanged?.Invoke();
    }

    private Visibility ViewVisibility(View target) => view == target ? Visibility.Visible : Visibility.Collapsed;

    internal PlaybackChange UpdateMedia(MediaReading value)
    {
        var now = Environment.TickCount64;
        var direction = trackIntent is { } intent && now <= intent.Expires ? intent.Direction : TrackDirection.None;
        var change = PlaybackMotion.Observe(reading, value, (now - receivedAt) / 1000d, direction,
            seeking || now <= seekIntentUntil || seekDebounce.IsEnabled || Seek.IsMouseCaptureWithin);
        if (PlaybackMotion.ReplacesTrack(change))
        {
            trackIntent = null;
            mediaCommandVersion++;
        }
        var oldHeightState = (reading.HasTrack, reading.Duration > 0, ArtistName.Visibility, MusicStatus.Text);
        if (PlaybackMotion.ReplacesTrack(change) || change is PlaybackChange.Restarted or PlaybackChange.Resumed)
        {
            seekDebounce.Stop();
            MusicStatus.Text = "";
        }
        reading = value;
        receivedAt = now;
        TrackName.Text = value.HasTrack ? value.Title : "Nothing playing";
        var sourceApp = ArtworkPalette.FormatSourceApp(value.Source);
        var subtitle = !value.HasTrack
            ? "Start playback in a media app."
            : string.IsNullOrEmpty(value.Artist) ? sourceApp ?? ""
            : sourceApp is not null ? $"{value.Artist} · {sourceApp}" : value.Artist;
        ArtistName.Text = subtitle;
        ArtistName.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        TrackName.ToolTip = value.HasTrack ? value.Title : null;
        ArtistName.ToolTip = string.IsNullOrEmpty(subtitle) ? null : subtitle;
        AlbumArt.Visibility = value.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
        if ((AlbumArt.Fill as ImageBrush)?.ImageSource != value.Artwork)
        {
            AlbumArt.Fill = value.Artwork is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : null;
            UpdateAccent(value.Artwork);
        }
        else if (!value.HasTrack)
        {
            UpdateAccent(null);
        }
        Previous.IsEnabled = value.CanPrevious;
        Toggle.IsEnabled = value.CanToggle;
        Next.IsEnabled = value.CanNext;
        Transport.Visibility = value.HasTrack ? Visibility.Visible : Visibility.Collapsed;
        Seek.IsEnabled = value.CanSeek;
        Timeline.Visibility = value.HasTrack && value.Duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        Seek.Opacity = value.CanSeek ? 1 : 0.55;
        var label = value.Playing ? "Pause" : "Play";
        Toggle.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(Toggle, label);
        if (change == PlaybackChange.Ended && value.HasTrack) MusicStatus.Text = "Playback ended";
        if (change != PlaybackChange.None) AnimatePlayback(change);
        UpdateProgress();
        if (view == View.Music && oldHeightState != (value.HasTrack, value.Duration > 0, ArtistName.Visibility, MusicStatus.Text))
            OnViewChanged();
        return change;
    }

    private void UpdateAccent(BitmapSource? artwork)
    {
        var accent = ArtworkPalette.ExtractAccent(artwork);
        CurrentAccent = accent;
        Resources["LiveAccent"] = CreateFrozenBrush(accent);
        ArtGlowStop.Color = Color.FromArgb(0x46, accent.R, accent.G, accent.B);
        var targetOpacity = artwork is null ? 0 : 1;
        SetGlyphOpacity(ArtGlow, targetOpacity, IsVisible && SystemParameters.ClientAreaAnimation);
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void AnimatePlayback(PlaybackChange change)
    {
        var animate = MusicView.IsVisible && SystemParameters.ClientAreaAnimation;
        SetGlyphOpacity(PlayGlyph, reading.Playing ? 0 : 1, animate);
        SetGlyphOpacity(PauseGlyph, reading.Playing ? 1 : 0, animate);
        var scale = reading.HasTrack && !reading.Playing ? .92 : 1;
        PlaybackMotion.EaseTo(artworkScale, ScaleTransform.ScaleXProperty, scale, 180, animate);
        PlaybackMotion.EaseTo(artworkScale, ScaleTransform.ScaleYProperty, scale, 180, animate);
        if (PlaybackMotion.ReplacesTrack(change))
        {
            var offset = PlaybackMotion.Offset(change, 9);
            var duration = change == PlaybackChange.Advanced ? 240 : 190;
            PlaybackMotion.Arrive(ArtTile, offset, duration);
            PlaybackMotion.Arrive(TrackDetails, offset, duration);
        }
        else if (change == PlaybackChange.Restarted)
            PlaybackMotion.Arrive(Timeline, -5, 160, .72);
    }

    private static void SetGlyphOpacity(UIElement glyph, double target, bool animate)
    {
        var current = glyph.Opacity;
        glyph.Opacity = target;
        glyph.BeginAnimation(OpacityProperty, animate && Math.Abs(current - target) > .01
            ? PlaybackMotion.To(current, target, 125) : null);
    }

    private void UpdateProgress()
    {
        if (seekDebounce.IsEnabled || seeking || Seek.IsMouseCaptureWithin) return;
        var elapsed = reading.Playing ? (Environment.TickCount64 - receivedAt) / 1000d * reading.PlaybackRate : 0;
        var position = Math.Clamp(reading.Position + elapsed, 0, reading.Duration);
        updatingSeek = true;
        Seek.Maximum = Math.Max(1, reading.Duration);
        Seek.Value = position;
        updatingSeek = false;
        Elapsed.Text = reading.Duration > 0 ? FormatTime(position) : "";
        Duration.Text = reading.Duration > 0
            ? showRemainingTime ? "-" + FormatTime(Math.Max(0, reading.Duration - position)) : FormatTime(reading.Duration)
            : "";
    }

    private void ToggleRemainingTime(object sender, MouseButtonEventArgs e)
    {
        showRemainingTime = !showRemainingTime;
        UpdateProgress();
        e.Handled = true;
    }

    internal void SyncVolume()
    {
        if (VolumeSlider.IsMouseCaptureWithin) return;
        var current = audio.Read();
        audioAvailable = current.HasValue;
        VolumeRow.IsEnabled = audioAvailable;
        VolumeRow.Opacity = audioAvailable ? 1 : 0.45;
        if (current is var (level, muted))
        {
            audioMuted = muted;
            ApplyVolumeUi(level, muted);
        }
        else
        {
            VolumePercent.Text = "—";
        }
    }

    internal (double Level, bool Muted)? AdjustVolume(double delta)
    {
        var updated = audio.Adjust(delta);
        if (updated is var (level, muted))
        {
            audioAvailable = true;
            audioMuted = muted;
            ApplyVolumeUi(level, muted);
        }
        return updated;
    }

    private void ApplyVolumeUi(double level, bool muted)
    {
        var percent = (int)Math.Round(level * 100);
        updatingVolume = true;
        VolumeSlider.Value = percent;
        updatingVolume = false;
        VolumePercent.Text = muted ? "Muted" : $"{percent}%";
        var iconKey = muted || percent == 0 ? "VolumeMuteIcon"
            : percent < 50 ? "VolumeLowIcon" : "VolumeHighIcon";
        VolumeIcon.Data = (Geometry)FindResource(iconKey);
        MuteButton.ToolTip = muted ? "Unmute (Ctrl+M)" : "Mute (Ctrl+M)";
    }

    private void ToggleMute(object sender, RoutedEventArgs e)
    {
        if (!audioAvailable) return;
        var next = !audioMuted;
        if (audio.SetMuted(next)) SyncVolume();
    }

    private void VolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || updatingVolume || !audioAvailable) return;
        var level = Math.Clamp(e.NewValue / 100d, 0, 1);
        if (audio.SetLevel(level))
        {
            audioMuted = false;
            ApplyVolumeUi(level, false);
        }
    }

    private async void PreviousTrack(object sender, RoutedEventArgs e) => await SendMediaCommand("previous");
    private async void ToggleTrack(object sender, RoutedEventArgs e) => await SendMediaCommand("toggle");
    private async void NextTrack(object sender, RoutedEventArgs e) => await SendMediaCommand("next");

    private async Task SendMediaCommand(string action, double seconds = 0)
    {
        var command = ++mediaCommandVersion;
        MusicStatus.Text = "";
        if (view == View.Music) OnViewChanged();
        var direction = action switch
        {
            "previous" => TrackDirection.Backward,
            "next" => TrackDirection.Forward,
            _ => TrackDirection.None
        };
        var intent = (direction, Environment.TickCount64 + 2_000);
        if (direction != TrackDirection.None) trackIntent = intent;
        if (action == "seek") seekIntentUntil = intent.Item2;
        if (!await media.Control(action, seconds) && command == mediaCommandVersion)
        {
            if (trackIntent == intent) trackIntent = null;
            MusicStatus.Text = action == "seek" ? "Couldn’t change playback position." : "This player didn’t respond.";
            if (view == View.Music) OnViewChanged();
        }
    }

    private async void FinishSeek(object sender, MouseButtonEventArgs e) => await CommitSeek();

    private async Task CommitSeek()
    {
        if (!seekDebounce.IsEnabled || seeking) return;
        seekDebounce.Stop();
        if (!Seek.IsEnabled) return;
        seeking = true;
        try { await SendMediaCommand("seek", Seek.Value); }
        finally { seeking = false; }
    }

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || updatingSeek || !Seek.IsEnabled) return;
        Elapsed.Text = FormatTime(e.NewValue);
        if (showRemainingTime && reading.Duration > 0)
            Duration.Text = "-" + FormatTime(Math.Max(0, reading.Duration - e.NewValue));
        seekDebounce.Stop();
        seekDebounce.Start();
    }

    internal void Tick()
    {
        UpdateTimer();
        if (IsVisible)
        {
            UpdateProgress();
            if (view == View.Music) SyncVolume();
        }
        if (copyFeedbackUntil != 0 && Environment.TickCount64 >= copyFeedbackUntil)
        {
            copyFeedbackUntil = 0;
            CopyClip.Content = "Copy";
        }
    }

    private void AddFiles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "Add files to the shelf" };
        pickerOpen = true;
        try
        {
            if (picker.ShowDialog(Window.GetWindow(this)) == true) AddPaths(picker.FileNames);
        }
        finally { pickerOpen = false; }
    }

    internal void AddPaths(string[] paths)
    {
        SetStatus(ShelfStatus, null);
        var skipped = false;
        foreach (var path in paths)
        {
            if (shelf.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, path))) continue;
            if (shelf.Count >= 32) { skipped = true; break; }
            if (File.Exists(path) || Directory.Exists(path)) shelf.Add(ShelfItem.FromPath(path));
            else skipped = true;
        }
        RefreshShelf();
        if (skipped) SetStatus(ShelfStatus, shelf.Count >= 32 ? "Shelf full. Remove a file to add another." : "Some files are no longer available.");
        ShowView(View.Shelf);
    }

    private void RefreshShelf()
    {
        ShelfEmpty.Visibility = shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfCount.Text = shelf.Count == 1 ? "1 file" : $"{shelf.Count} files";
        UpdateShelfButtons();
    }

    private void UpdateShelfButtons()
    {
        var hasSelection = Files.SelectedItem is ShelfItem;
        RevealFile.IsEnabled = hasSelection;
        RemoveFile.IsEnabled = hasSelection;
        ClearShelf.IsEnabled = shelf.Count > 0;
    }

    private void SetStatus(TextBlock target, string? message)
    {
        target.Text = message ?? "";
        target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        OnViewChanged();
    }

    private void AddDroppedFiles(object sender, DragEventArgs e)
    {
        DropSurface.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        e.Handled = true;
    }

    private void ShowDropEffect(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Link) : DragDropEffects.None;
        DropSurface.BorderBrush = e.Effects != DragDropEffects.None ? (Brush)FindResource("Accent") : Brushes.Transparent;
        e.Handled = true;
    }

    private void LeaveDropTarget(object sender, DragEventArgs e) => DropSurface.BorderBrush = Brushes.Transparent;

    private void RemoveSelectedFile(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is ShelfItem item) shelf.Remove(item);
        SetStatus(ShelfStatus, null);
        RefreshShelf();
    }

    private void ClearAllShelf(object sender, RoutedEventArgs e)
    {
        shelf.Clear();
        SetStatus(ShelfStatus, null);
        RefreshShelf();
    }

    private void RevealSelectedFile(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is not ShelfItem item) return;
        if (!(File.Exists(item.Path) || Directory.Exists(item.Path)))
        {
            SetStatus(ShelfStatus, "File missing. Remove it and add it again.");
            return;
        }
        try
        {
            var args = item.IsDirectory ? $"\"{item.Path}\"" : $"/select,\"{item.Path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            SetStatus(ShelfStatus, null);
        }
        catch (System.ComponentModel.Win32Exception) { SetStatus(ShelfStatus, "Couldn’t open File Explorer."); }
    }

    private void OpenSelectedFile(object sender, MouseButtonEventArgs e)
        => OpenSelectedFile();

    private void OpenSelectedFileWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelectedFile(sender, e);
        else if (e.Key == Key.Enter) OpenSelectedFile();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Files.SelectedItem is ShelfItem item)
        {
            try
            {
                Clipboard.SetText(item.Path);
                SetStatus(ShelfStatus, "Path copied.");
            }
            catch (ExternalException) { SetStatus(ShelfStatus, "Clipboard busy. Try copying again."); }
        }
        else return;
        e.Handled = true;
    }

    private void OpenSelectedFile()
    {
        if (Files.SelectedItem is not ShelfItem item) return;
        if (!(File.Exists(item.Path) || Directory.Exists(item.Path)))
        {
            SetStatus(ShelfStatus, "File missing. Remove it and add it again.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            SetStatus(ShelfStatus, null);
        }
        catch (System.ComponentModel.Win32Exception) { SetStatus(ShelfStatus, "Couldn’t open this file. Try File Explorer."); }
    }

    private void StartFileDrag(object sender, MouseButtonEventArgs e) => fileDragOrigin = e.GetPosition(Files);

    private void DragFile(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || fileDragOrigin is not { } origin || Files.SelectedItem is not ShelfItem item)
            return;
        var offset = e.GetPosition(Files) - origin;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        fileDragOrigin = null;
        if (!(File.Exists(item.Path) || Directory.Exists(item.Path))) return;
        fileDragActive = true;
        try
        {
            DragDrop.DoDragDrop(Files, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy);
        }
        finally { fileDragActive = false; }
    }

    private void ClipboardPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var enabled = ClipboardEnabled.IsChecked == true;
        listenToClipboard(enabled);
        if (!enabled)
        {
            clipboard.Clear();
            ClipFilter.Text = "";
        }
        SetStatus(ClipboardStatus, null);
        RefreshClipboardView();
    }

    internal void RecordClipboard()
    {
        if (ClipboardEnabled.IsChecked != true) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 32_768) return;
            var existingIndex = clipboard.FindIndex(item => StringComparer.Ordinal.Equals(item.Text, text));
            var pinned = existingIndex >= 0 && clipboard[existingIndex].Pinned;
            if (existingIndex >= 0) clipboard.RemoveAt(existingIndex);

            var entry = new ClipboardEntry(text, DateTimeOffset.UtcNow, pinned);
            var insertAt = pinned ? 0 : clipboard.TakeWhile(item => item.Pinned).Count();
            clipboard.Insert(insertAt, entry);
            if (clipboard.Count > 24)
            {
                var unpinned = clipboard.FindLastIndex(item => !item.Pinned);
                clipboard.RemoveAt(unpinned >= 0 ? unpinned : clipboard.Count - 1);
            }
            RefreshClipboardView();
        }
        catch (ExternalException) { }
    }

    private void ClipFilterChanged(object sender, TextChangedEventArgs e)
    {
        ClipFilterHint.Visibility = string.IsNullOrEmpty(ClipFilter.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshClipboardView();
    }

    private void RefreshClipboardView()
    {
        var selectedText = (Clips.SelectedItem as ClipboardEntry)?.Text;
        var query = ClipFilter.Text.Trim();
        visibleClipboard.Clear();
        foreach (var entry in clipboard.Where(item => string.IsNullOrEmpty(query)
            || item.Text.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            visibleClipboard.Add(entry);
        }
        if (selectedText is not null)
            Clips.SelectedItem = visibleClipboard.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.Text, selectedText));

        ClipFilterBox.Visibility = clipboard.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateClipboardEmpty();
        UpdateClipboardButtons();
    }

    private void UpdateClipboardButtons()
    {
        var selected = Clips.SelectedItem as ClipboardEntry;
        CopyClip.IsEnabled = selected is not null;
        PinClip.IsEnabled = selected is not null;
        PinClip.Content = selected?.Pinned == true ? "Unpin" : "Pin";
        ClearClips.IsEnabled = clipboard.Count > 0;
    }

    private void TogglePinClip(object sender, RoutedEventArgs e)
    {
        if (Clips.SelectedItem is not ClipboardEntry selected) return;
        var index = clipboard.FindIndex(item => StringComparer.Ordinal.Equals(item.Text, selected.Text));
        if (index < 0) return;
        var updated = selected with { Pinned = !selected.Pinned };
        clipboard.RemoveAt(index);
        var insertAt = updated.Pinned ? 0 : clipboard.TakeWhile(item => item.Pinned).Count();
        clipboard.Insert(insertAt, updated);
        RefreshClipboardView();
        Clips.SelectedItem = updated;
    }

    private void CopySelected(object sender, RoutedEventArgs e)
    {
        if (Clips.SelectedItem is not ClipboardEntry item) return;
        try
        {
            Clipboard.SetText(item.Text);
            CopyClip.Content = "Copied";
            copyFeedbackUntil = Environment.TickCount64 + 1800;
            SetStatus(ClipboardStatus, null);
        }
        catch (ExternalException) { SetStatus(ClipboardStatus, "Clipboard busy. Try copying again."); }
    }

    private void CopyClipWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Clips.SelectedItem is ClipboardEntry item)
        {
            clipboard.RemoveAll(entry => StringComparer.Ordinal.Equals(entry.Text, item.Text));
            SetStatus(ClipboardStatus, null);
            RefreshClipboardView();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter && !(e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)) return;
        CopySelected(sender, e);
        e.Handled = true;
    }

    private void ClearClipboard(object sender, RoutedEventArgs e)
    {
        var hasUnpinned = clipboard.Any(item => !item.Pinned);
        if (hasUnpinned) clipboard.RemoveAll(item => !item.Pinned);
        else clipboard.Clear();
        if (clipboard.Count == 0) ClipFilter.Text = "";
        SetStatus(ClipboardStatus, null);
        RefreshClipboardView();
    }

    private void UpdateClipboardEmpty()
    {
        ClipboardEmpty.Text = clipboard.Count > 0 && visibleClipboard.Count == 0
            ? "No matching clips."
            : ClipboardEnabled.IsChecked == true ? "Copy text to add it here." : "Turn on history to keep copied text here.";
        ClipboardEmpty.Visibility = visibleClipboard.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearClips.IsEnabled = clipboard.Count > 0;
    }

    private void TimerDurationChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || timerEndsAt != 0) return;
        if (sender is RadioButton { Tag: string value } && int.TryParse(value, out var minutes))
            selectedDuration = minutes * 60;
        remaining = selectedDuration;
        timerCompleted = false;
        UpdateTimer();
    }

    private void ToggleTimer(object sender, RoutedEventArgs e)
    {
        if (timerEndsAt != 0)
        {
            remaining = Math.Max(0, (timerEndsAt - Environment.TickCount64) / 1000d);
            timerEndsAt = 0;
        }
        else
        {
            if (remaining <= 0) remaining = selectedDuration;
            timerEndsAt = Environment.TickCount64 + (long)(remaining * 1000);
            timerCompleted = false;
        }
        UpdateTimer();
    }

    private void AddTimerMinute(object sender, RoutedEventArgs e)
    {
        if (timerEndsAt != 0)
        {
            timerEndsAt += 60_000;
            remaining = Math.Max(0, (timerEndsAt - Environment.TickCount64) / 1000d);
        }
        else
        {
            remaining += 60;
        }
        if (remaining > selectedDuration) selectedDuration = remaining;
        timerCompleted = false;
        UpdateTimer();
    }

    private void ResetTimer(object sender, RoutedEventArgs e)
    {
        timerEndsAt = 0;
        remaining = selectedDuration;
        timerCompleted = false;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (timerEndsAt != 0)
        {
            remaining = Math.Max(0, (timerEndsAt - Environment.TickCount64) / 1000d);
            if (remaining <= 0)
            {
                timerEndsAt = 0;
                timerCompleted = true;
                System.Media.SystemSounds.Exclamation.Play();
            }
        }
        TimerReadout.Text = timerCompleted ? "Time’s up" : FormatTime(Math.Ceiling(remaining), true);
        var paused = timerEndsAt == 0 && !timerCompleted && remaining < selectedDuration;
        TimerButton.Content = timerEndsAt != 0 ? "Pause timer" : paused ? "Resume timer" : timerCompleted ? "Start again" : "Start timer";
        TimerState.Text = timerCompleted ? "Finished" : timerEndsAt != 0 ? "Counting down" : paused ? "Paused" : "Ready";
        TimerProgress.Value = selectedDuration > 0 ? Math.Clamp(remaining / selectedDuration, 0, 1) : 0;
        TimerAddMinute.IsEnabled = timerEndsAt != 0 || paused;
        TimerReset.IsEnabled = timerEndsAt != 0 || remaining < selectedDuration || timerCompleted;
        TimerMinutes.IsEnabled = timerEndsAt == 0;
    }

    internal string? CompactTimerText => timerCompleted ? "Time’s up"
        : timerEndsAt != 0 ? FormatTime(Math.Ceiling(remaining), true) : null;
    internal double CompactTimerProgress => timerEndsAt != 0 && selectedDuration > 0
        ? Math.Clamp(remaining / selectedDuration, 0, 1) : 0;
    internal bool TimerCompleted => timerCompleted;

    private static string FormatTime(double seconds, bool padMinutes = false)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss")
            : time.ToString(padMinutes ? @"mm\:ss" : @"m\:ss");
    }

    public void Dispose()
    {
        ViewTransitionLayer.Children.Clear();
        seekDebounce.Stop();
        media.Dispose();
        audio.Dispose();
    }
}
